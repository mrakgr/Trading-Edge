"""Return autocorrelation of one ticker-day on N-second bars, first hour vs the rest (user 2026-11-20, ANT 2026-11-19).

The trade filters are the 1s-bar convention (TradingEdge.Orb/Bars.fs, docs/stock_data_schema.md §2), mirrored here in SQL:
  IN-BAR print (open/high/low/close, vwap, std, volume, n_trades) — all venues, TRF included:
      size > 0 AND price > 0 AND no official open/close MARKER (15, 16) AND
      ( an auction print (17, 25, 19, 8) — kept unconditionally
        OR ( published within 50 ms of execution (sip - participant <= 50 ms, at ms precision; skipped if either is 0)
             AND no excluded condition (2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53) ) )
      clock = sip_timestamp (participant_timestamp when sip is 0)
  SHARE columns (total_volume, retail_*) — the FlushFader retail-gate rule (Bars.fs "THE SHARE COLUMNS"):
      size > 0 AND price > 0 AND no marker AND NO auction print AND no excluded condition — NO 50 ms cap (late
      prints count toward who traded). Retail = BJZZ: an exchange-4 (TRF/ADF) print whose sub-tick fraction f (exact,
      in integer micro-dollars; the tick is $0.01 at >= $1, $0.0001 below) is in (0.6, 1) = buy, (0, 0.4) = sell.
      retail_share = retail_volume / total_volume.  (No mid columns: user 2026-11-20.)
  Plus one guard the 1s bars do not have: an in-bar print more than --max-dev (10 %) from the median of its 101 in-bar
  neighbours is dropped (SPCX 2026-06-12: on-time, condition-free 100-share prints at the $135 IPO price with the
  stock at $169, 20 % off). 2 % was too tight: it cut real prints in an IPO's opening minute (ANT 110-115 inside
  30 s) and 300k-share blocks in a fast decline. Logged; it changes no bar close on SPCX or ANT.
Session 09:30-16:00 ET; an IPO's leading empty bars are trimmed (the first hour = the stock's first hour). A bar with
no in-bar print carries the previous close/vwap (zero return).

Statistics per window (first hour, the rest, and each clock hour), on log returns of close and of vwap:
  ACF lag 1..5 with its ~1/sqrt(n) standard error; VR(10) = Var(5-min move) / (10 Var(30 s move)); ER = |net| / path.
⚠ VWAP-to-VWAP returns are positively autocorrelated even for a random walk (Working 1960, ~+0.25 for even volume). The
"vwap null" column is that baseline CALIBRATED to this ticker's own volume pattern inside each bar: a 1 s random walk
weighted by the day's actual per-second volume, averaged over --sims simulated days. Read vwap ACF1 against the null,
close ACF1 against 0 (minus a little for bid-ask bounce).

  cd research && python scripts/analysis/autocorr_ticker.py -t ANT -d 2026-11-19
Writes data/autocorr/<T>_<D>_<N>s.parquet (bars) and data/autocorr/<T>_<D>_<N>s.html (chart).
"""
import argparse
import datetime
import os
import zoneinfo

import duckdb
import numpy as np
import pandas as pd
import plotly.graph_objects as go
from plotly.subplots import make_subplots
from scipy.stats import spearmanr

TR = 'data/bulk/trades'
# TradingEdge.Orb/TradeFilters.fs — keep in step with it
EXCLUDE = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53]::UTINYINT[]"
AUCTION = "[17, 25, 19, 8]::UTINYINT[]"
MARKER = "[15, 16]::UTINYINT[]"
MAX_SIP_DELTA_NS = 50_000_000
TRF_EXCHANGE = 4
NY = zoneinfo.ZoneInfo('America/New_York')
OPEN_S, CLOSE_S = 9 * 3600 + 1800, 16 * 3600
MAX_DEV = 0.10


def load(ticker: str, d: str) -> tuple[pd.DataFrame, int]:
    y, m, dd = map(int, d.split('-'))
    base = int(datetime.datetime(y, m, dd, tzinfo=NY).timestamp() * 1e9)
    lo, hi = base + OPEN_S * 10**9, base + CLOSE_S * 10**9
    ms = lambda c: f"(({c} // 1000000) * 1000000)"
    con = duckdb.connect()
    con.execute("SET threads=4; SET memory_limit='3GB'")
    X = con.execute(f"""
        WITH t AS (
          SELECT CASE WHEN sip_timestamp <> 0 THEN sip_timestamp ELSE participant_timestamp END ts,
                 price, size, exchange, conditions, sip_timestamp, participant_timestamp
          FROM read_parquet('{TR}/{d}.parquet')
          WHERE ticker = '{ticker}' AND size > 0 AND price > 0 AND NOT list_has_any(conditions, {MARKER}))
        SELECT ts, price, size,
               (list_has_any(conditions, {AUCTION})
                OR ((sip_timestamp = 0 OR participant_timestamp = 0
                     OR {ms('sip_timestamp')} - {ms('participant_timestamp')} <= {MAX_SIP_DELTA_NS})
                    AND NOT list_has_any(conditions, {EXCLUDE}))) AS inbar,
               (NOT list_has_any(conditions, {AUCTION}) AND NOT list_has_any(conditions, {EXCLUDE})) AS counts,
               CASE WHEN exchange <> {TRF_EXCHANGE} THEN 0.0
                    WHEN price >= 1 THEN (CAST(round(price * 1e6) AS BIGINT) % 10000) / 10000.0
                    ELSE (CAST(round(price * 1e6) AS BIGINT) % 100) / 100.0 END AS frac
        FROM t WHERE ts >= {lo} AND ts < {hi}
        ORDER BY ts""").df()
    con.close()
    # the outlier guard, on in-bar prints only (the share columns count volume, not price)
    ib = X.index[X.inbar]
    med = X.price[ib].rolling(101, center=True, min_periods=1).median()
    bad = ib[((X.price[ib] / med - 1).abs() > MAX_DEV).to_numpy()]
    if len(bad):
        print(f'dropped {len(bad)} in-bar prints more than {MAX_DEV:.0%} from the local median '
              f'({X["size"][bad].sum():,.0f} shares)')
        X.loc[bad, 'inbar'] = False
    return X, base


def bars(X: pd.DataFrame, base: int, sec: int) -> pd.DataFrame:
    n = (CLOSE_S - OPEN_S) // sec
    s = (X.ts.to_numpy(np.int64) - base) // 10**9 - OPEN_S
    X = X.assign(b=s // sec, s1=s)
    f = X.frac.to_numpy()
    X['rb'] = np.where(X.counts & (f > 0.6), X['size'], 0.0)
    X['rsl'] = np.where(X.counts & (f > 0) & (f < 0.4), X['size'], 0.0)
    X['tv'] = np.where(X.counts, X['size'], 0.0)
    I = X[X.inbar].assign(pq=lambda x: x.price * x['size'], p2q=lambda x: x.price ** 2 * x['size'])
    g = I.groupby('b')
    B = pd.DataFrame(index=pd.RangeIndex(n, name='bar'))
    B['open'], B['high'], B['low'], B['close'] = g.price.first(), g.price.max(), g.price.min(), g.price.last()
    B['volume'] = g['size'].sum()
    B['vwap'] = g.pq.sum() / B.volume
    B['std'] = np.sqrt(np.maximum(g.p2q.sum() / B.volume - B.vwap ** 2, 0))
    B['n_trades'] = g.size()
    S = X.groupby('b')
    B['total_volume'] = S.tv.sum()
    B['retail_buy'] = S.rb.sum()
    B['retail_sell'] = S.rsl.sum()
    cols = ['volume', 'n_trades', 'total_volume', 'retail_buy', 'retail_sell']
    B[cols] = B[cols].fillna(0)
    B['retail_volume'] = B.retail_buy + B.retail_sell
    B['retail_share'] = B.retail_volume / B.total_volume.replace(0, np.nan)
    B['no_print'] = B.close.isna()
    B['close'] = B.close.ffill().bfill()
    B['vwap'] = B.vwap.ffill().bfill()
    for c in ('open', 'high', 'low'):
        B[c] = B[c].fillna(B.close)
    B['time'] = [(datetime.datetime(2000, 1, 1) + datetime.timedelta(seconds=OPEN_S + i * sec)).strftime('%H:%M:%S')
                 for i in range(n)]
    # per-second in-bar volume inside each bar — the weights for the calibrated VWAP null
    W = np.zeros((n, sec))
    np.add.at(W, (I.b.to_numpy(), I.s1.to_numpy() - I.b.to_numpy() * sec), I['size'].to_numpy())
    return B.reset_index(), W


def acf(r: np.ndarray, k: int) -> float:
    r = r - r.mean()
    return float((r[k:] * r[:-k]).sum() / (r * r).sum())


def vr(r: np.ndarray, n: int) -> float:
    rn = np.convolve(r, np.ones(n), mode='valid')
    return float(rn.var() / (n * r.var()))


def vwap_null(W: np.ndarray, sims: int, seed: int = 1) -> np.ndarray:
    """VWAP-to-VWAP returns of `sims` simulated days, shape (sims, n-1): a 1 s random walk (no autocorrelation at all)
    averaged inside each bar with the day's real per-second volume as weights. Each window slices its own columns."""
    rng = np.random.default_rng(seed)
    n, sec = W.shape
    wsum = W.sum(axis=1, keepdims=True)
    Wn = np.where(wsum > 0, W / np.where(wsum > 0, wsum, 1), 0.0)
    out = np.empty((sims, n - 1))
    for i in range(sims):
        x = np.cumsum(rng.standard_normal(n * sec)).reshape(n, sec)
        v = np.where(wsum[:, 0] > 0, (x * Wn).sum(axis=1), np.nan)
        v = pd.Series(v).ffill().bfill().to_numpy()                 # empty bars: carry, as the real bars do
        out[i] = np.diff(v)
    return out


def stats(B: pd.DataFrame, R_null: np.ndarray, lo: int, hi: int, label: str) -> dict:
    """The returns with both ends inside bars [lo, hi): bar i vs bar i-1 for i in (lo, hi)."""
    a = lo + 1
    rc = np.diff(np.log(B.close.to_numpy()))[a - 1:hi - 1]
    rv = np.diff(np.log(B.vwap.to_numpy()))[a - 1:hi - 1]
    nul = np.array([acf(R_null[s, a - 1:hi - 1], 1) for s in range(R_null.shape[0])])
    seg = B.iloc[lo:hi]
    row = dict(window=label, bars=hi - lo, empty=int(seg.no_print.sum()),
               se=1 / np.sqrt(len(rc)),
               close_acf1=acf(rc, 1), close_rank1=float(spearmanr(rc[1:], rc[:-1])[0]), vwap_acf1=acf(rv, 1), vwap_null=float(nul.mean()),
               vwap_excess=acf(rv, 1) - float(nul.mean()))
    for k in (2, 3, 4, 5):
        row[f'close_acf{k}'] = acf(rc, k)
    row['close_vr10'] = vr(rc, 10)
    row['close_er'] = float(abs(rc.sum()) / np.abs(rc).sum())
    row['move_pct'] = float(np.expm1(rc.sum()) * 100)
    row['vol_30s_bp'] = float(rc.std() * 1e4)
    row['volume'] = float(seg.volume.sum())
    row['retail_share'] = float(seg.retail_volume.sum() / seg.total_volume.sum())
    row['retail_imb'] = float((seg.retail_buy.sum() - seg.retail_sell.sum()) / max(seg.retail_volume.sum(), 1))
    return row


def chart(B: pd.DataFrame, sec: int, split: int, title: str, path: str):
    rc = np.log(B.close).diff()
    rv = np.log(B.vwap).diff()
    win = split
    roll_c = rc.rolling(win).apply(lambda r: acf(r.to_numpy(), 1), raw=False)
    roll_v = rv.rolling(win).apply(lambda r: acf(r.to_numpy(), 1), raw=False)
    t = B.time
    fig = make_subplots(rows=4, cols=1, shared_xaxes=True, row_heights=[0.5, 0.15, 0.15, 0.2], vertical_spacing=0.025,
                        subplot_titles=('', 'volume: retail (BJZZ) vs other', 'retail share of volume',
                                        f'lag-1 ACF, trailing {win} bars ({win * sec // 60} min)'))
    fig.add_trace(go.Candlestick(x=t, open=B.open, high=B.high, low=B.low, close=B.close, name='lit OHLC',
                                 increasing_line_color='#2e8b57', decreasing_line_color='#c0392b'), row=1, col=1)
    fig.add_trace(go.Scatter(x=t, y=B.vwap, mode='lines', line=dict(color='#1f6fb2', width=1), name='bar VWAP'), row=1, col=1)
    fig.add_trace(go.Scatter(x=t, y=B.vwap + B['std'], mode='lines', line=dict(color='#1f6fb2', width=0.5, dash='dot'),
                             name='VWAP ± std', showlegend=True), row=1, col=1)
    fig.add_trace(go.Scatter(x=t, y=B.vwap - B['std'], mode='lines', line=dict(color='#1f6fb2', width=0.5, dash='dot'),
                             showlegend=False), row=1, col=1)
    fig.add_trace(go.Bar(x=t, y=B.retail_volume, name='retail volume', marker_color='#e67e22'), row=2, col=1)
    fig.add_trace(go.Bar(x=t, y=B.total_volume - B.retail_volume, name='other volume', marker_color='#95a5a6'), row=2, col=1)
    fig.add_trace(go.Scatter(x=t, y=B.retail_share.rolling(10, min_periods=1).mean(), mode='lines',
                             line=dict(color='#e67e22', width=1), name='retail share (5-min mean)'), row=3, col=1)
    fig.add_trace(go.Scatter(x=t, y=roll_c, mode='lines', line=dict(color='#333333', width=1.2), name='ACF1 close'), row=4, col=1)
    fig.add_trace(go.Scatter(x=t, y=roll_v, mode='lines', line=dict(color='#1f6fb2', width=1.2), name='ACF1 vwap'), row=4, col=1)
    fig.add_hline(y=0, line=dict(color='#999999', dash='dot'), row=4, col=1)
    fig.update_yaxes(range=[0, 1.2 * float(np.nanpercentile(B.total_volume, 99))], row=2, col=1)   # the opening cross dwarfs the rest
    fig.add_vrect(x0=t.iloc[0], x1=t.iloc[split], fillcolor='#1f6fb2', opacity=0.06, line_width=0, row=1, col=1)
    fig.update_layout(barmode='stack', height=1100, width=1400, title=title, xaxis_rangeslider_visible=False,
                      hovermode='x unified')
    viz = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'visualization', 'chart_controls.js')
    with open(viz) as f:
        post_script = f.read()
    fig.write_html(path, config={'scrollZoom': True, 'displayModeBar': True}, post_script=post_script)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('-t', '--ticker', required=True)
    ap.add_argument('-d', '--date', required=True)
    ap.add_argument('--bar-sec', type=int, default=30)
    ap.add_argument('--split-min', type=int, default=60, help='first window length in minutes (default 60)')
    ap.add_argument('--skip-bars', type=int, default=2,
                    help='opening bars left out of every window that starts at the first bar (default 2 = the first '
                         'minute: the opening cross and its spike-and-reverse dominate an hour of products)')
    ap.add_argument('--max-dev', type=float, default=0.10,
                    help='drop prints further than this from the median of their 101 neighbours (default 0.10)')
    ap.add_argument('--sims', type=int, default=300, help='simulated days for the calibrated VWAP null')
    ap.add_argument('-o', '--out-dir', default='data/autocorr')
    a = ap.parse_args()

    sec = a.bar_sec
    global MAX_DEV
    MAX_DEV = a.max_dev
    X, base = load(a.ticker, a.date)
    if X.empty:
        raise SystemExit(f'no trades for {a.ticker} on {a.date}')
    B, W = bars(X, base, sec)
    # an IPO (or a halt at the bell) opens late: the session starts at the first lit print, so "the first hour" is
    # the stock's first hour of trading. A normal day has a print in the 09:30 bar and nothing is trimmed.
    f = int(np.argmax(~B.no_print.to_numpy()))
    if f > 0:
        print(f'first print in the {B.time[f]} bar: {f} leading bars trimmed')
        B, W = B.iloc[f:].reset_index(drop=True), W[f:]
    R0 = vwap_null(W, a.sims)
    n, split = len(B), a.split_min * 60 // sec
    k = a.skip_bars
    rows = [stats(B, R0, k, split, f'first {a.split_min} min'), stats(B, R0, split, n, 'rest of day'),
            stats(B, R0, k, n, 'whole day'), stats(B, R0, 0, split, f'first {a.split_min} min, no skip')]
    hour = 3600 // sec
    for h in range(0, n, hour):
        rows.append(stats(B, R0, h + (k if h == 0 else 0), min(h + hour, n),
                          f'{B.time[h][:5]}-{B.time[min(h + hour, n) - 1][:5]}'))
    S = pd.DataFrame(rows)

    os.makedirs(a.out_dir, exist_ok=True)
    stem = f'{a.out_dir}/{a.ticker}_{a.date}_{sec}s'
    B.to_parquet(stem + '.parquet', index=False)
    S.to_csv(stem + '_stats.csv', index=False)
    first = S.iloc[0]; rest = S.iloc[1]
    chart(B, sec, split, f'{a.ticker} {a.date} — {sec} s bars.  ACF1 close: first {a.split_min} min {first.close_acf1:+.3f}, '
                         f'rest {rest.close_acf1:+.3f} (±{2 * first.se:.2f} / ±{2 * rest.se:.2f}, 2 s.e.).  '
                         f'VWAP ACF1 minus its null: {first.vwap_excess:+.3f} / {rest.vwap_excess:+.3f}', stem + '.html')

    pd.set_option('display.width', 250)
    pd.set_option('display.max_columns', 40)
    print(f'{a.ticker} {a.date}: {len(X):,} prints, {n} bars of {sec} s, {int(B.no_print.sum())} empty; '
          f'retail share {B.retail_volume.sum() / B.total_volume.sum():.1%}\n')
    print(f'windows starting at the first bar leave out its first {k} bars (--skip-bars); se = 1/sqrt(returns)\n')
    cols1 = ['window', 'bars', 'empty', 'se', 'close_acf1', 'close_rank1', 'close_acf2', 'close_acf3', 'close_acf4', 'close_acf5',
             'close_vr10', 'close_er']
    cols2 = ['window', 'vwap_acf1', 'vwap_null', 'vwap_excess', 'move_pct', 'vol_30s_bp', 'volume', 'retail_share',
             'retail_imb']
    print(S[cols1].to_string(index=False, float_format=lambda v: f'{v:+.3f}'))
    print()
    print(S[cols2].to_string(index=False, float_format=lambda v: f'{v:+.3f}'))
    print(f'\nSaved {stem}.parquet, {stem}_stats.csv, {stem}.html')


if __name__ == '__main__':
    main()
