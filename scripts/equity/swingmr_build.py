#!/usr/bin/env python3
"""SWING MR — the sampler dataset (user 2026-11-02: "revisit equity swing mean reversion and perhaps try using the
efficiency ratio and z score on stocks above the 200d MA and with the 60d median dollar volume above 1m ... I don't think
we ever tried z scores and eff ratios of varying lengths and thresholds").

One row per UNIVERSE ticker-day D (no signal gate: the study filters post hoc). The universe (all knowable at D's close):
  close·n > its 200-session SMA (D included) · the MEDIAN raw dollar volume (close × volume) of the PRIOR 60 sessions
  ≥ $1M (D excluded) · raw close ≥ $5 (user ruling) · ticker_reference type CS / ADRC (daily_episodes_causal) · 2005+.
PRICES per docs/price_adjustment.md §2: cn = close·n is comparable across splits; a return t1 → t2 =
[(P2·n2 − P1·n1) + (C2 − C1)] / (P1·n1); the floor on the RAW close. Episodes (a > 45-day gap) never share a window.
FEATURES at D's close (D included):
  z_N    = (ln cn − mean ln cn) / std ln cn over the last N closes, N = 3, 5, 10, 20, 50   (z ≤ 0 = below its mean)
  er_N   = SIGNED Kaufman efficiency (cn_D − cn_{D−N}) / Σ |Δcn| over the last N changes, N = 2, 3, 5, 10, 20
  ret_1/3/5, rsi2 (Wilder, on cn), dist200 / dist50 (cn ÷ the SMA − 1), atr20 (the prior 20 days' mean (H−L)/C),
  rvol (D's dollar volume ÷ the prior 20 days' mean), med60 (the gate's median dollar volume), price (raw close)
  ma_N   = cn ÷ its N-session SMA − 1 (D included), N = 3, 5, 10, 20, 50, 100, 200 (user 2026-11-02: "Distance from the 72h MA
           worked really well in crypto perpetuals on the long side ... try a variety of them") — % distance, NOT
           volatility-scaled (z_N is the scaled one)
BREADTH (user 2026-11-02: "how many stocks in our dataset that pass the liquidity filters are above their 20d MA in
  percentage terms") → <out>/../breadth.parquet, one row per date: of the LIQUID stocks that day (the same med60 and $5
  gates, ANY trend), the share closing above their 20 / 50 / 200-session SMA (each over the names with that SMA defined).
  Measured at D's close — the study also uses D−1's (knowable before D's close).
OUTCOMES (lookahead BY DESIGN — never gate), bp, dividends included:
  rc_k = from D's CLOSE to D+k's close, k = 1, 2, 3, 5, 7, 10 (live: a 15:59 decision, the Snoozers' convention)
  ro_k = from D+1's OPEN to D+k's close (fully causal on daily bars; the dividend ex at D+1's open is not received)
  ro_0 = D+1's open vs D's close (the overnight)
  x1 = the first close above its 5-day SMA (Connors), x2 = the first close above the previous day's HIGH, each capped at
  10 days: x?_k = the days held, x?_date = the exit session, x?_rc / x?_ro = the return from the close / the open entry.
  An episode that ends first exits at its last close (x?_end = 1).

  cd research && python -u scripts/equity/swingmr_build.py > logs/swingmr_build.log 2>&1; echo exit $?
"""
import argparse
import os
import shutil
import sys
import time

import duckdb
import numpy as np
import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import data_v2  # noqa: E402

ap = argparse.ArgumentParser()
ap.add_argument('--out', default='data/equity/swingmr/rows')
ap.add_argument('--batch', type=int, default=1500)
ap.add_argument('--min-price', type=float, default=5.0)
ap.add_argument('--min-med-dv', type=float, default=1e6)
ap.add_argument('--tickers', default=None, help='Test: only these tickers, comma-separated.')
a = ap.parse_args()
T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, flush=True)

con = data_v2.attach(duckdb.connect(), 'main')
con.execute("SET memory_limit='3GB'; SET threads=4")
tickers = [r[0] for r in con.execute("SELECT DISTINCT ticker FROM daily_episodes_causal ORDER BY 1").fetchall()]
if a.tickers:
    tickers = [t for t in tickers if t in set(a.tickers.split(','))]
log(f'{len(tickers):,} CS/ADRC tickers')
if os.path.isdir(a.out):
    shutil.rmtree(a.out)
os.makedirs(a.out)
ZN, EN, KS = [3, 5, 10, 20, 50], [2, 3, 5, 10, 20], [1, 2, 3, 5, 7, 10]
MAN = [3, 5, 10, 20, 50, 100, 200]
BREADTH = []                                          # per batch: date → (liquid names, above 20 / 50 / 200, with each SMA)


def roll(x, w, fn):
    return getattr(pd.Series(x).rolling(w, min_periods=w), fn)().to_numpy()


def one(g):
    N = len(g)
    if N < 201:
        return None
    o, h, l, c, v, n, cd = (g[k].to_numpy(np.float64) for k in ('open', 'high', 'low', 'close', 'volume', 'n', 'cum_div'))
    cn = c * n
    if not np.all(cn > 0):
        bad = ~(cn > 0)
        cn = np.where(bad, np.nan, cn)
    lc = np.log(cn)
    dv = c * v                                       # raw dollar volume (scale-invariant)
    ma = {w: roll(cn, w, 'mean') for w in MAN}
    ma200, ma50, ma5 = ma[200], ma[50], ma[5]
    med60 = np.r_[np.nan, roll(dv, 60, 'median')[:-1]]               # the PRIOR 60 sessions
    liq = (med60 >= a.min_med_dv) & (c >= a.min_price)
    if liq.any():
        jl = np.flatnonzero(liq)
        b = {'date': g.date.to_numpy()[jl], 'liq': np.ones(len(jl), np.int32)}
        for w in (20, 50, 200):
            m = ma[w][jl]
            b[f'has{w}'] = np.isfinite(m).astype(np.int32)
            b[f'up{w}'] = (cn[jl] > m).astype(np.int32)
        BREADTH.append(pd.DataFrame(b))
    uni = (cn > ma200) & (med60 >= a.min_med_dv) & (c >= a.min_price) & (np.arange(N) < N - 1)
    if not uni.any():
        return None
    i = np.flatnonzero(uni)
    out = {'ticker': g.ticker.iat[0], 'date': g.date.to_numpy()[i], 'price': c[i], 'med60': med60[i],
           'dist200': cn[i] / ma200[i] - 1, 'dist50': cn[i] / ma50[i] - 1}
    for w in MAN:
        out[f'ma_{w}'] = cn[i] / ma[w][i] - 1
    for w in ZN:
        m, s = roll(lc, w, 'mean'), roll(lc, w, 'std')
        with np.errstate(all='ignore'):
            out[f'z_{w}'] = ((lc - m) / s)[i]
    dabs = np.abs(np.diff(cn, prepend=np.nan))
    for w in EN:
        path = roll(dabs, w, 'sum')
        lag = np.r_[np.full(w, np.nan), cn[:-w]]
        with np.errstate(all='ignore'):
            out[f'er_{w}'] = ((cn - lag) / path)[i]
    for w in (1, 3, 5):
        lag = np.r_[np.full(w, np.nan), cn[:-w]]
        out[f'ret_{w}'] = (cn / lag - 1)[i]
    d = np.diff(cn, prepend=np.nan)
    up, dn = pd.Series(np.clip(d, 0, None)), pd.Series(np.clip(-d, 0, None))
    au, ad = up.ewm(alpha=0.5, adjust=False).mean().to_numpy(), dn.ewm(alpha=0.5, adjust=False).mean().to_numpy()
    with np.errstate(all='ignore'):
        out['rsi2'] = (100 - 100 / (1 + au / ad))[i]
    out['atr20'] = np.r_[np.nan, roll((h - l) / c, 20, 'mean')[:-1]][i]
    out['rvol'] = (dv / np.r_[np.nan, roll(dv, 20, 'mean')[:-1]])[i]
    # outcomes
    def ret(i0, p0, j):                               # from price p0 (raw, at row i0's scale n[i0]) to row j's close
        return 1e4 * ((cn[j] - p0 * n[i0]) + (cd[j] - cd[i0])) / (p0 * n[i0])
    nxt = np.minimum(i + 1, N - 1)
    out['ro_0'] = 1e4 * ((o[nxt] * n[nxt] - cn[i]) + (cd[nxt] - cd[i])) / cn[i]
    for k in KS:
        j = i + k
        ok = j < N
        jj = np.minimum(j, N - 1)
        rc = ret(i, c[i], jj)
        ro = ret(nxt, o[nxt], jj)
        out[f'rc_{k}'] = np.where(ok, rc, np.nan)
        out[f'ro_{k}'] = np.where(ok, ro, np.nan)
    # the rule exits (≤ 10 days; the episode's last close if it ends first)
    prevh = np.r_[np.nan, h * n][:-1]
    c1 = cn > ma5
    c2 = cn > prevh
    for nm, cond in (('x1', c1), ('x2', c2)):
        kk = np.full(len(i), 10)
        end = np.zeros(len(i), np.int8)
        for q, i0 in enumerate(i):
            hit = np.flatnonzero(cond[i0 + 1:min(i0 + 11, N)])
            if len(hit):
                kk[q] = hit[0] + 1
            elif i0 + 10 >= N:
                kk[q] = N - 1 - i0
                end[q] = 1
        j = i + kk
        out[f'{nm}_k'] = kk.astype(np.int8)
        out[f'{nm}_date'] = g.date.to_numpy()[j]
        out[f'{nm}_end'] = end
        out[f'{nm}_rc'] = ret(i, c[i], j)
        out[f'{nm}_ro'] = np.where(kk >= 1, ret(nxt, o[nxt], j), np.nan)
    return pd.DataFrame(out)


part, total = 0, 0
for b0 in range(0, len(tickers), a.batch):
    tk = pd.DataFrame({'ticker': tickers[b0:b0 + a.batch]})
    con.register('tk', tk)
    X = con.execute("""SELECT ticker, episode, date, open, high, low, close, volume, n, cum_div FROM daily_episodes_causal
                       WHERE ticker IN (SELECT ticker FROM tk) ORDER BY ticker, episode, date""").df()
    con.unregister('tk')
    rows = [r for _, g in X.groupby(['ticker', 'episode'], sort=False) if (r := one(g)) is not None]
    if rows:
        D = pd.concat(rows, ignore_index=True)
        D = D[D.date >= np.datetime64('2005-01-01')]
        D.to_parquet(os.path.join(a.out, f'part_{part:03d}.parquet'), index=False)
        part += 1
        total += len(D)
    del X
    if BREADTH:                                       # collapse this batch's breadth rows to per-date counts
        Bb = pd.concat(BREADTH).groupby('date').sum()
        BREADTH.clear()
        BREADTH.append(Bb.reset_index())
    log(f'  {min(b0 + a.batch, len(tickers)):,}/{len(tickers):,} tickers, {total:,} rows')
B = pd.concat(BREADTH).groupby('date').sum().reset_index()
for w in (20, 50, 200):
    B[f'br{w}'] = B[f'up{w}'] / B[f'has{w}']
B = B.sort_values('date')
for w in (20, 50, 200):
    B[f'br{w}_prev'] = B[f'br{w}'].shift(1)
bp = os.path.join(os.path.dirname(a.out.rstrip('/')), 'breadth.parquet')
B.to_parquet(bp, index=False)
log(f'wrote {a.out}: {total:,} rows in {part} parts; {bp}: {len(B):,} dates')
