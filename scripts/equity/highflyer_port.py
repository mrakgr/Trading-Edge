"""HighFlyer v3 PORTED to a script that runs in either price scale (user 2026-11-01: "we'll have to run it on data that
is causal only").

The engine (TradingEdge.HighFlyer, defaultConfig) reads split_adjusted_prices — BACK-adjusted prices, rescaled by every
LATER split and dividend (the CLAUDE.md lookahead class: a $1 floor on them selects future reverse splits). This port
re-implements its production entry and exit exactly (TradingEdge.HighFlyer/Types.fs, QullaSystem):
  every gate reads the state of the PRIOR bars (snapshots before the bar is folded in), on the signal bar i:
    move     c[i] / c[i−1] − 1 ∈ [0.10, 0.30)
    rvol     v[i] / mean(v over the bars dated [d−28 days, d))  >= 5            (CalendarMeanMa(28))
    history  barsSeen > 21, counted AFTER the signal bar is folded in (i >= 21)
    adv      mean(c·v over the bars dated [d−28 days, d)) >= $100k
    52w      c[i] >= 0.95 × max(c over the prior 252 bars)                       (MaxMa: a partial window counts)
    price    c[i] >= $1
    tight    (max(h, prior 14) − min(l, prior 14)) / mean(linear TR, prior 14) < 4.5
    ATR%     mean(log TR, the prior 14 TRs) < 0.10;   TR = max(h, pc) vs min(l, pc), skipped where undefined
    fade     c[i] / o[i] − 1 >= −0.07
    runner   max(the 14-TR log ATR over its last 126 values, before bar i) >= 0.04
  entry = the signal bar's close; exit = the open of bar i + 6 (5 holding bars, then the next open); a trip without
  bar i + 6 is marked at the last close (open = 1). Uncapped: every signal opens its own trip.
--scale back    split_adjusted_prices, exactly as the engine (the CONTROL: must reproduce its trips)
--scale causal  daily_adjusted (raw P, causal n, cum_div; docs/price_adjustment.md):
                  prices compared across time (the channel, ATR, tightness, runner) on P·n
                  the move and the trade's return = the HOLDER's return [(P2·n2 − P1·n1) + (C2 − C1)] / (P1·n1)
                  volume in original shares v / n (rvol); dollar volume P·v (raw); the $1 floor and the fade on raw P
Universe: ticker_reference type CS / ADRC (semi-join), dates 2003-09-10 .. --end.

  cd research && python -u scripts/equity/highflyer_port.py --scale back > logs/highflyer_port_back.log 2>&1; echo exit $?
"""
import argparse, sys, time
import numpy as np, pandas as pd, duckdb
ap = argparse.ArgumentParser()
ap.add_argument("--scale", choices=["back", "causal"], required=True)
ap.add_argument("--start", default="2003-09-10")
ap.add_argument("--end", default="2026-05-13")
ap.add_argument("--out", default=None)
ap.add_argument("--batch", type=int, default=1500)
a = ap.parse_args()
OUT = a.out or f"data/equity/highflyer_repro/port_{a.scale}.parquet"
T0 = time.time()
def log(*x): print(f"[{time.time()-T0:7.1f}s]", *x, flush=True)

con = duckdb.connect("data/trading.db", read_only=True)
con.execute("set memory_limit='3GB'; set threads=4")
SRC = ("SELECT ticker, date, adj_open o, adj_high h, adj_low l, adj_close c, adj_volume::DOUBLE v, 1.0 n, 0.0 cd "
       "FROM split_adjusted_prices p" if a.scale == "back" else
       "SELECT ticker, date, open o, high h, low l, close c, volume v, n, cum_div cd FROM daily_adjusted p")
tickers = [r[0] for r in con.execute(f"""
    SELECT DISTINCT p.ticker FROM ({SRC}) p
    WHERE EXISTS (SELECT 1 FROM ticker_reference r WHERE r.ticker = p.ticker AND r.type IN ('CS','ADRC'))
    ORDER BY 1""").fetchall()]
log(f"{a.scale}: {len(tickers):,} CS/ADRC tickers")

def roll_max_prior(x, w):        # max of x[i−w .. i−1] (a partial window counts); NaN at i = 0
    return pd.Series(x).rolling(w, min_periods=1).max().shift(1).values
def roll_min_prior(x, w):
    return pd.Series(x).rolling(w, min_periods=1).min().shift(1).values

def one(g):
    d = g.date.values.astype("datetime64[D]"); N = len(d)
    o, h, l, c, v, n, cd = (g[k].values.astype(np.float64) for k in ("o", "h", "l", "c", "v", "n", "cd"))
    if a.scale == "back":
        O, H, L, Cx = o, h, l, c                       # the engine's one scale
        vol, dol = v, c * v
    else:
        O, H, L, Cx = o * n, h * n, l * n, c * n       # P·n: comparable across splits
        vol, dol = v / n, c * v
    pc = np.r_[np.nan, Cx[:-1]]
    ok = (np.arange(N) >= 1) & (H > 0) & (L > 0) & (pc > 0)
    hi_e, lo_e = np.fmax(H, pc), np.fmin(L, pc)
    j = np.flatnonzero(ok)
    atrlog_after = np.full(N, np.nan); atrlin_after = np.full(N, np.nan); maxatr_after = np.full(N, np.nan)
    if len(j):
        al = pd.Series(np.log(hi_e[j] / lo_e[j])).rolling(14, min_periods=1).mean()
        atrlog_after[j] = al.values
        atrlin_after[j] = pd.Series(hi_e[j] - lo_e[j]).rolling(14, min_periods=1).mean().values
        maxatr_after[j] = al.rolling(126, min_periods=1).max().values
    snap = lambda x: np.r_[np.nan, pd.Series(x).ffill().values[:-1]]      # the state before bar i is folded in
    atrlog, atrlin, maxatr = snap(atrlog_after), snap(atrlin_after), snap(maxatr_after)
    hi252 = roll_max_prior(Cx, 252)
    rng = roll_max_prior(H, 14) - roll_min_prior(L, 14)
    # the calendar means over the bars dated [d − 28, d)
    lo = np.searchsorted(d, d - np.timedelta64(28, "D"), "left")
    cv, cdl = np.r_[0.0, np.cumsum(vol)], np.r_[0.0, np.cumsum(dol)]
    cnt = np.arange(N) - lo
    with np.errstate(invalid="ignore", divide="ignore"):
        avgv = np.where(cnt > 0, (cv[np.arange(N)] - cv[lo]) / cnt, np.nan)
        adv = np.where(cnt > 0, (cdl[np.arange(N)] - cdl[lo]) / cnt, np.nan)
        if a.scale == "back":
            move = np.where(pc != 0, c / np.r_[np.nan, c[:-1]] - 1, np.nan)
        else:
            c1, n1, k1 = np.r_[np.nan, c[:-1]], np.r_[np.nan, n[:-1]], np.r_[np.nan, cd[:-1]]
            move = ((c * n - c1 * n1) + (cd - k1)) / (c1 * n1)
        rvol = np.where(avgv != 0, vol / avgv, np.nan)
        tight = np.where(atrlin != 0, rng / atrlin, np.nan)
        fade = np.where(o > 0, c / o - 1, np.nan)
    E = ((move >= 0.10) & (move < 0.30) & (rvol >= 5.0) & (np.arange(N) >= 21) & (adv >= 100_000.0)
         & (Cx >= 0.95 * hi252) & (c >= 1.0) & (tight < 4.5) & (atrlog < 0.10) & (fade >= -0.07) & (maxatr >= 0.04))
    i = np.flatnonzero(E)
    if not len(i): return None
    x = i + 6; opn = x >= N; xi = np.where(opn, N - 1, x)
    px_x = np.where(opn, c[xi], o[xi])
    if a.scale == "back":
        ret = px_x / c[i] - 1
    else:
        ret = ((px_x * n[xi] - c[i] * n[i]) + (cd[xi] - cd[i])) / (c[i] * n[i])
    return pd.DataFrame({"symbol": g.ticker.values[0], "entry_date": d[i], "exit_date": d[xi], "entry_price": c[i],
                         "exit_price": px_x, "ret": ret, "open": opn.astype(np.int8), "pct_up": move[i], "rvol": rvol[i],
                         "adv": adv[i], "atr_log": atrlog[i], "tightness": tight[i], "pct_52w": Cx[i] / hi252[i] - 1,
                         "max_atr_log": maxatr[i], "fade": fade[i], "split_in_hold": (n[xi] != n[i]).astype(np.int8)})

parts = []
for b0 in range(0, len(tickers), a.batch):
    tk = pd.DataFrame({"ticker": tickers[b0:b0 + a.batch]}); con.register("tk", tk)
    X = con.execute(f"""SELECT * FROM ({SRC}) p WHERE p.ticker IN (SELECT ticker FROM tk)
                        AND p.date >= DATE '{a.start}' AND p.date <= DATE '{a.end}' ORDER BY ticker, date""").df()
    con.unregister("tk")
    for _, g in X.groupby("ticker", sort=False):
        r = one(g)
        if r is not None: parts.append(r)
    log(f"  {min(b0 + a.batch, len(tickers)):,}/{len(tickers):,} tickers, {sum(len(p) for p in parts):,} trips")
T = pd.concat(parts, ignore_index=True)
T.to_parquet(OUT + ".tmp"); import os; os.replace(OUT + ".tmp", OUT)
log(f"wrote {OUT}: {len(T):,} trips")
