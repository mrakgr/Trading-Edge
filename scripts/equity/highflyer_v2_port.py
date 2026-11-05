"""HighFlyerV2 (the 10:00 ET partial-candle entry) PORTED to a script that runs in either price scale (user 2026-11-05:
"Yes" — re-measure the 10:00 system on causal data, as v5 did for the daily one; docs/highflyer_v2_results.md Run 31).

The engine (TradingEdge.HighFlyerV2 --partial-entry) reads split_adjusted_prices (BACK-adjusted) and puts the RAW 10:00
partial candle on that scale with adj_close / raw_close — the CLAUDE.md lookahead class. Two leaks:
  the $1 floor   on the back-adjusted 10:00 price (admits names whose RAW price is under $1 but reverse-split LATER)
  rvol           the partial candle's RAW volume over the mean of the BACK-ADJUSTED daily volumes: before a future 1-for-k
                 reverse split the baseline is k× too small, so rvol is k× too big (and a future forward split deflates it)
This port re-implements the engine exactly (Types.fs HighFlyer + Backtest.fs DbEmitter):
  feed           CS / ADRC (semi-join), dates [--start, --end] (2005-01-01 .. 2026-05-13: the indicators warm up from --start)
  episodes       a > 45-calendar-day gap between consecutive bars MTM-closes the open trips at the last pre-gap close and
                 restarts every indicator cold (the gap-reset, Run 30)
  snapshots      every gate reads the state BEFORE the day's daily bar is folded in:
    move         10:00 close / the previous daily close − 1 ∈ [--up, 0.30)
    rvol         10:00 volume (premarket-inclusive, 04:00–09:59) / mean(daily volume, the prior 20 bars; a partial window
                 counts) >= --rvol-min
    history      barsSeen > 21 (episode index >= 21)
    adv          mean(close · volume, prior 20 bars) >= $100k
    52w          10:00 close >= 0.95 × max(close, prior 252 bars)
    price        10:00 close >= $1
    tight        (max(high, prior 14) − min(low, prior 14)) / mean(linear TR, the prior 14 TRs) < 4.5
    ATR%         mean(log TR, the prior 14 TRs) < 0.10 (TR skipped where undefined)
    fade         10:00 close / 10:00 open − 1 >= −0.07
    runner       max(the 14-TR log ATR over its last 126 values) >= 0.04
  trade          fill at the 10:00 close; exit at the open of episode bar i + 6, else MTM at the episode's last close
                 (open = 1). A day without a usable 10:00 candle (no RTH minute before 10:00) is not entered.
--entry close    the engine's parity path instead (the daily bar is the entry candle, rvol >= 5): the shared-trades control
--scale back     split_adjusted_prices exactly as the engine (the CONTROL: must reproduce its CSV)
--scale causal   daily_adjusted (raw P, causal n, cum_div; docs/price_adjustment.md): history compared on P·n, the 10:00
                 candle at P·n(D); volume in original shares (v / n) for rvol, the 10:00 volume / n(D); dollar volume raw P·v;
                 the $1 floor and the fade on raw P; the move and the trade = the HOLDER's return.
The 10:00 candles: the archived partial_candle_1000 (db_archive/2026-09-29; raw minute aggs, built 2026-06/07).
    cd research && python3 -u scripts/equity/highflyer_v2_port.py --scale back > logs/highflyer_v2_port_back.log 2>&1; echo exit $?
"""
import argparse, os, time
import numpy as np, pandas as pd, duckdb
ap = argparse.ArgumentParser()
ap.add_argument("--scale", choices=["back", "causal"], required=True)
ap.add_argument("--entry", choices=["partial", "close"], default="partial")
ap.add_argument("--rvol-min", type=float, default=None)       # partial 1.0 (the production 10:00 book), close 5.0
ap.add_argument("--up", type=float, default=0.10)
ap.add_argument("--start", default="2005-01-01")
ap.add_argument("--end", default="2026-05-13")
ap.add_argument("--partial", default="/mnt/d/trading-edge-bulk/db_archive/2026-09-29/partial_candle_1000.parquet")
ap.add_argument("--out", default=None)
ap.add_argument("--batch", type=int, default=1500)
a = ap.parse_args()
RVMIN = a.rvol_min if a.rvol_min is not None else (1.0 if a.entry == "partial" else 5.0)
OUT = a.out or f"data/equity/highflyer_repro/v2port_{a.entry}_{a.scale}.parquet"
T0 = time.time()
def log(*x): print(f"[{time.time()-T0:7.1f}s]", *x, flush=True)

con = duckdb.connect("data/trading.db", read_only=True)
con.execute("set memory_limit='3GB'; set threads=4")
con.execute("CREATE TEMP VIEW pc AS SELECT * FROM read_parquet([" + ", ".join(f"'{x}'" for x in a.partial.split(",")) + "])")   # comma-separated: the archive + the extension
if a.scale == "back":
    SRC = """SELECT p.ticker, p.date, p.adj_open o, p.adj_high h, p.adj_low l, p.adj_close c, p.adj_volume::DOUBLE v,
                    1.0 n, 0.0 cd, d.close raw_c FROM split_adjusted_prices p JOIN daily_prices d ON d.ticker = p.ticker AND d.date = p.date"""
else:
    SRC = "SELECT ticker, date, open o, high h, low l, close c, volume::DOUBLE v, n, cum_div cd, close raw_c FROM daily_adjusted p"
tickers = [r[0] for r in con.execute(f"""
    SELECT DISTINCT p.ticker FROM ({SRC}) p
    WHERE EXISTS (SELECT 1 FROM ticker_reference r WHERE r.ticker = p.ticker AND r.type IN ('CS','ADRC'))
      AND p.date >= DATE '{a.start}' AND p.date <= DATE '{a.end}' ORDER BY 1""").fetchall()]
log(f"{a.scale} / {a.entry}: {len(tickers):,} CS/ADRC tickers, rvol >= {RVMIN}, move >= {a.up}")

def prior(s): return s.shift(1).values
def episode(g):
    d = g.date.values.astype("datetime64[D]"); N = len(d)
    o, h, l, c, v, n, cd, rc = (g[k].values.astype(np.float64) for k in ("o", "h", "l", "c", "v", "n", "cd", "raw_c"))
    po, ph, pl, pcl, pv = (g[k].values.astype(np.float64) for k in ("po", "ph", "pl", "pcl", "pv"))   # raw 10:00 candle
    if a.scale == "back":
        O, H, L, Cx = o, h, l, c
        vol, dol = v, c * v
    else:
        O, H, L, Cx = o * n, h * n, l * n, c * n
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
    snap = lambda x: np.r_[np.nan, pd.Series(x).ffill().values[:-1]]
    atrlog, atrlin, maxatr = snap(atrlog_after), snap(atrlin_after), snap(maxatr_after)
    hi252 = prior(pd.Series(Cx).rolling(252, min_periods=1).max())
    rng = prior(pd.Series(H).rolling(14, min_periods=1).max()) - prior(pd.Series(L).rolling(14, min_periods=1).min())
    avgv = prior(pd.Series(vol).rolling(20, min_periods=1).mean())
    adv = prior(pd.Series(dol).rolling(20, min_periods=1).mean())
    # the entry candle
    if a.entry == "close":
        E_o, E_c, E_c_raw, E_o_raw, E_v = O, Cx, (c if a.scale == "causal" else c), o, vol
        has = np.ones(N, bool)
    else:
        has = np.isfinite(po) & (rc != 0)
        if a.scale == "back":
            r = c / rc                                   # adj_close / raw_close (the engine's rescale)
            E_o, E_c, E_v = po * r, pcl * r, pv          # RAW 10:00 volume against the adjusted baseline (as the engine)
            E_c_raw, E_o_raw = pcl * r, po * r           # the engine's floor and fade read the rescaled prices
        else:
            E_o, E_c, E_v = po * n, pcl * n, pv / n      # P·n(D); volume in original shares
            E_c_raw, E_o_raw = pcl, po
    with np.errstate(invalid="ignore", divide="ignore"):
        if a.scale == "back":
            move = E_c / pc - 1
        else:
            c1, n1, k1 = np.r_[np.nan, c[:-1]], np.r_[np.nan, n[:-1]], np.r_[np.nan, cd[:-1]]
            ec_raw = c if a.entry == "close" else pcl
            move = ((ec_raw * n - c1 * n1) + (cd - k1)) / (c1 * n1)
        rvol = np.where(avgv != 0, E_v / avgv, np.nan)
        tight = np.where(atrlin != 0, rng / atrlin, np.nan)
        fade = np.where(E_o_raw > 0, E_c_raw / E_o_raw - 1, np.nan)
    E = (has & (move >= a.up) & (move < 0.30) & (rvol >= RVMIN) & (np.arange(N) >= 21) & (adv >= 100_000.0)
         & (E_c >= 0.95 * hi252) & (E_c_raw >= 1.0) & (tight < 4.5) & (atrlog < 0.10) & (fade >= -0.07) & (maxatr >= 0.04))
    i = np.flatnonzero(E)
    if not len(i): return None
    x = i + 6; opn = x >= N; xi = np.where(opn, N - 1, x)
    px_x = np.where(opn, c[xi], o[xi])                     # raw (causal) or adjusted (back) exit price
    if a.scale == "back":
        ent = E_c[i]; ret = px_x / ent - 1
    else:
        ent = (c if a.entry == "close" else pcl)[i]
        ret = ((px_x * n[xi] - ent * n[i]) + (cd[xi] - cd[i])) / (ent * n[i])
    return pd.DataFrame({"symbol": g.ticker.values[0], "entry_date": d[i], "exit_date": d[xi], "entry_price": ent,
                         "exit_price": px_x, "ret": ret, "open": opn.astype(np.int8), "pct_up": move[i], "rvol": rvol[i],
                         "adv": adv[i], "atr_log": atrlog[i], "tightness": tight[i], "pct_52w": E_c[i] / hi252[i] - 1,
                         "max_atr_log": maxatr[i], "fade": fade[i], "raw_open_1000": po[i], "raw_high_1000": ph[i],
                         "raw_low_1000": pl[i], "raw_close_1000": pcl[i], "raw_vol_1000": pv[i], "n": n[i],
                         "split_in_hold": (n[xi] != n[i]).astype(np.int8)})

parts = []
for b0 in range(0, len(tickers), a.batch):
    tk = pd.DataFrame({"ticker": tickers[b0:b0 + a.batch]}); con.register("tk", tk)
    X = con.execute(f"""SELECT p.*, pc.open po, pc.high ph, pc.low pl, pc.close pcl, pc.volume::DOUBLE pv
                        FROM ({SRC}) p LEFT JOIN pc ON pc.ticker = p.ticker AND pc.date = p.date
                        WHERE p.ticker IN (SELECT ticker FROM tk) AND p.date >= DATE '{a.start}' AND p.date <= DATE '{a.end}'
                        ORDER BY p.ticker, p.date""").df()
    con.unregister("tk")
    for _, g in X.groupby("ticker", sort=False):
        dd = g.date.values.astype("datetime64[D]")
        ep = np.r_[0, np.cumsum(np.diff(dd).astype(np.int64) > 45)]
        for _, e in g.groupby(ep, sort=False):
            r = episode(e)
            if r is not None: parts.append(r)
    log(f"  {min(b0 + a.batch, len(tickers)):,}/{len(tickers):,} tickers, {sum(len(p) for p in parts):,} trips")
T = pd.concat(parts, ignore_index=True)
T.to_parquet(OUT + ".tmp"); os.replace(OUT + ".tmp", OUT)
log(f"wrote {OUT}: {len(T):,} trips")
