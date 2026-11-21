"""VWAP TREND on 30 s bars (user 2026-11-20): long while vwap(t) > vwap(t-5), short while lower; fill = the NEXT bar's vwap.

The rule, per ticker-day:
  - 30 s bars from the session start (09:30, or an IPO's first print, floored to 30 s) to 16:00; vwap per bar.
  - At the end of bar t: s = sign(vwap_t - vwap_{t-L}) (L = --lookback, 5). s = 0 keeps the current position.
    An empty bar's vwap is the last known one (it is a signal, so only the past may be used).
  - The position flips at the vwap of bar t + d (d = --delay, 1): the average price over the next 30 s. An empty fill
    bar fills at the next bar that trades.
  - The first signal is the bar ENDING --start-min (5) after the session start; nothing before.
  - Windows: "first hour" flattens at the vwap of the bar starting 60 min after the session start; "rest" trades from
    60 min (first signal = the bar ending at 60 min) to the close; "day" from 5 min to the close (flat at the last bar).
P&L per trade = direction x log(exit / entry), one unit of notional, no costs. Break-even cost per SIDE = mean trade / 2.

The CONTROL (null): the same rule on a 1 s random walk averaged inside each bar with the day's real per-second volume —
VWAP fills on a path with no autocorrelation at all. Its mean must be ~0; if not, the VWAP fill itself manufactures P&L.

Data: data/intraday_1s_slim (the 1s convention) when the day is there, else the trades tape through
scripts/analysis/autocorr_ticker.py (the same filters, rebuilt in SQL).

  cd research && python scripts/analysis/vwap_trend_30s.py --tapesim ~/tapesim-website/docs/gap-play-examples.md \
      --play ANT:2026-11-19 --play SPCX:2026-06-12 > logs/vwap_trend_30s.log 2>&1; echo exit $?
"""
import argparse
import os
import re
import sys
import time

import duckdb
import numpy as np
import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import autocorr_ticker as act  # noqa: E402

S1 = 'data/intraday_1s_slim'
OPEN_S, CLOSE_S = 34200, 57600
BAR = 30


def seconds(ticker: str, d: str) -> tuple[pd.DataFrame, str]:
    """1 s rows (bucket = seconds since 00:00 ET, vwap, volume) for 09:30-16:00."""
    p = f'{S1}/{d}.parquet'
    if os.path.exists(p):
        con = duckdb.connect()
        X = con.execute(f"SELECT bucket, vwap::DOUBLE vwap, volume::DOUBLE volume FROM read_parquet('{p}') "
                        f"WHERE ticker = ? AND bucket >= {OPEN_S} AND bucket < {CLOSE_S} AND volume > 0 ORDER BY bucket",
                        [ticker]).df()
        con.close()
        return X, '1s'
    if not os.path.exists(f'{act.TR}/{d}.parquet'):
        return pd.DataFrame(columns=['bucket', 'vwap', 'volume']), 'none'
    T, base = act.load(ticker, d)
    T = T[T.inbar]
    b = (T.ts.to_numpy(np.int64) - base) // 10**9
    T = T.assign(bucket=b, pq=T.price * T['size'])
    g = T.groupby('bucket')
    X = pd.DataFrame({'vwap': g.pq.sum() / g['size'].sum(), 'volume': g['size'].sum()}).reset_index()
    return X, 'trades'


def bars(X: pd.DataFrame):
    """vwap per 30 s bar from the session start; the per-second volume matrix (for the null)."""
    start = OPEN_S + (int(X.bucket.iloc[0]) - OPEN_S) // BAR * BAR
    n = (CLOSE_S - start) // BAR
    b = (X.bucket.to_numpy() - start) // BAR
    pv = np.bincount(b, X.vwap.to_numpy() * X.volume.to_numpy(), n)
    v = np.bincount(b, X.volume.to_numpy(), n)
    W = np.zeros((n, BAR))
    W[b, X.bucket.to_numpy() - start - b * BAR] = X.volume.to_numpy()
    with np.errstate(invalid='ignore', divide='ignore'):
        vw = np.where(v > 0, pv / v, np.nan)
    return start, vw, W


def run(vw: np.ndarray, t0: int, end: int, L: int, d: int):
    """Trades for signals at bars t0..end-d-1 (fills at t+d < end), flat at the vwap of bar `end`.
    Returns (n_trades, list of trade log-returns signed)."""
    n = len(vw)
    known = pd.Series(vw).ffill().to_numpy()          # the signal: last known vwap
    fill = pd.Series(vw).bfill().ffill().to_numpy()   # the fill: the next bar that trades
    end = min(end, n - 1)
    pos, entry, out = 0, 0.0, []
    for t in range(t0, end - d):
        if t - L < 0 or np.isnan(known[t]) or np.isnan(known[t - L]):
            continue
        s = np.sign(known[t] - known[t - L])
        if s == 0 or s == pos:
            continue
        px = fill[t + d]
        if pos != 0:
            out.append(pos * np.log(px / entry))
        pos, entry = int(s), px
    if pos != 0:
        out.append(pos * np.log(fill[end] / entry))
    return out


def null_paths(W: np.ndarray, sims: int, rng) -> np.ndarray:
    n = W.shape[0]
    ws = W.sum(axis=1, keepdims=True)
    Wn = np.where(ws > 0, W / np.where(ws > 0, ws, 1), 0)
    out = np.empty((sims, n))
    for i in range(sims):
        x = np.cumsum(rng.standard_normal(n * BAR)).reshape(n, BAR) * 1e-3   # ~0.1 %/s random walk, log units
        out[i] = np.where(ws[:, 0] > 0, np.exp((x * Wn).sum(axis=1)), np.nan) * 100
    return out


def parse_tapesim(path: str) -> pd.DataFrame:
    rows = []
    for line in open(path):
        m = re.match(r'\|\s*(\d{4}-\d{2}-\d{2})\s*\|\s*([A-Z.]+)\s*\|\s*([+-][\d.]+)%\s*\|\s*([\d.]+)x\s*\|\s*(\d+)%\s*\|'
                     r'\s*([\d.]+)\s*\|\s*([\d.]+|n/a)\s*\|', line)
        if m:
            rows.append(dict(date=m[1], ticker=m[2], gap=float(m[3]), rvol=float(m[4]), pm_adv=float(m[5]),
                             score=float(m[6]), atr_ratio=float('nan') if m[7] == 'n/a' else float(m[7]),
                             src='tapesim'))   # n/a: the list's last day — the ratio is forward-looking
    return pd.DataFrame(rows)


def summarize(P: pd.DataFrame, col: str, ncol: str) -> str:
    x = P[col].dropna()
    tr = P[ncol].sum()
    win, loss = x[x > 0].sum(), -x[x < 0].sum()
    pf = win / loss if loss > 0 else float('inf')
    per_trade = x.sum() / tr * 1e4 if tr else float('nan')
    return (f'days {len(x):3d}  sum {x.sum() * 100:+8.1f} %  mean/day {x.mean() * 100:+6.2f} %  median {x.median() * 100:+6.2f} %  '
            f'up-days {(x > 0).mean():5.1%}  PF(days) {pf:5.2f}  trades {tr:6.0f} ({tr / max(len(x), 1):4.1f}/day)  '
            f'{per_trade:+6.2f} bp/trade  break-even {per_trade / 2:+6.2f} bp/side')


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--tapesim', help='the TapeSim gap-play-examples.md')
    ap.add_argument('--play', action='append', default=[], help='TICKER:YYYY-MM-DD, repeatable')
    ap.add_argument('--lookback', type=int, default=5)
    ap.add_argument('--delay', type=int, default=1)
    ap.add_argument('--start-min', type=int, default=5)
    ap.add_argument('--hour-min', type=int, default=60)
    ap.add_argument('--sims', type=int, default=50, help='null random-walk days per play')
    ap.add_argument('-o', '--out', default='data/vwap_trend/plays.csv')
    a = ap.parse_args()

    P = parse_tapesim(a.tapesim) if a.tapesim else pd.DataFrame()
    extra = pd.DataFrame([dict(ticker=p.split(':')[0], date=p.split(':')[1], src='extra') for p in a.play])
    P = pd.concat([extra, P], ignore_index=True)
    k0, k1 = a.start_min * 60 // BAR, a.hour_min * 60 // BAR
    rng = np.random.default_rng(1)
    rows, T0 = [], time.time()
    for i, r in enumerate(P.itertuples()):
        X, src = seconds(r.ticker, r.date)
        row = dict(date=r.date, ticker=r.ticker, data=src)
        if len(X) < 100:
            rows.append(row)
            print(f'{r.ticker} {r.date}: no data ({src})', flush=True)
            continue
        start, vw, W = bars(X)
        n = len(vw)
        wins = {'hour': (k0 - 1, k1), 'rest': (k1 - 1, n - 1), 'day': (k0 - 1, n - 1)}
        row.update(start=f'{start // 3600:02d}:{start % 3600 // 60:02d}', bars=n, empty=int(np.isnan(vw).sum()))
        for w, (t0, end) in wins.items():
            tr = run(vw, t0, end, a.lookback, a.delay)
            row[f'{w}_ret'], row[f'{w}_n'] = float(np.sum(tr)), len(tr)
            # buy-and-hold over the same span (fill to fill), signed by nothing: the window's own drift
            f = pd.Series(vw).bfill().ffill().to_numpy()
            row[f'{w}_bh'] = float(np.log(f[min(end, n - 1)] / f[t0 + a.delay]))
        Z = null_paths(W, a.sims, rng)
        for w, (t0, end) in wins.items():
            nt = [run(z, t0, end, a.lookback, a.delay) for z in Z]
            row[f'{w}_null'] = float(np.mean([np.sum(t) for t in nt]))
            row[f'{w}_null_n'] = float(np.mean([len(t) for t in nt]))
        rows.append(row)
        print(f'{i + 1:3d}/{len(P)} {r.ticker:5s} {r.date} [{src}] start {row["start"]}  first hour {row["hour_ret"] * 100:+6.2f} % '
              f'({row["hour_n"]} trades)  day {row["day_ret"] * 100:+6.2f} % ({row["day_n"]})  {time.time() - T0:.0f}s',
              flush=True)
    R = P.merge(pd.DataFrame(rows), on=['date', 'ticker'], how='left')
    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    R.to_csv(a.out, index=False)

    print(f'\nlookback {a.lookback} bars, fill delay {a.delay} bar, start {a.start_min} min after the session start\n')
    for name, Q in [('ANT / SPCX', R[R.src == 'extra']), ('TapeSim gap plays', R[R.src == 'tapesim'])]:
        Q = Q[Q.hour_n.notna()]
        if Q.empty:
            continue
        print(f'== {name}')
        for w, lab in [('hour', 'first hour '), ('rest', 'after 1 h   '), ('day', 'whole day  ')]:
            print(f'  {lab} {summarize(Q, f"{w}_ret", f"{w}_n")}')
            print(f'  {"  null":12s} {summarize(Q.assign(**{w + "_ret": Q[w + "_null"]}), f"{w}_ret", f"{w}_null_n")}')
        print()
    T = R[(R.src == 'tapesim') & R.hour_n.notna()]
    if len(T):
        print('== TapeSim, first hour, by group')
        for lab, m in [('gap up', T.gap > 0), ('gap down', T.gap < 0),
                       ('ATR ratio < 1.2', T.atr_ratio < 1.2), ('ATR ratio >= 1.2', T.atr_ratio >= 1.2),
                       ('2023', T.date < '2024'), ('2024', T.date.between('2024', '2025')),
                       ('2025-26', T.date >= '2025')]:
            print(f'  {lab:17s} {summarize(T[m], "hour_ret", "hour_n")}')
    print(f'\nSaved {a.out}')


if __name__ == '__main__':
    main()
