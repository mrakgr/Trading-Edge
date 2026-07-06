"""Render 1m charts of VwapReclaim trips so the entries can be judged QUALITATIVELY.

For each (symbol, trade_date) that produced a trip, load THAT day's minute_aggs
parquet (the exact source the engine trades off), split-adjust it by the trip's
adj_ratio, and render the 09:30->16:00 ET session as a 1m candlestick + volume
panel, overlaid with the reclaim's own geometry so you can SEE whether the setup
is behaving:

  * session VWAP (purple line)  — anchored 09:30, typical-price weighted, EXACTLY as
    the engine computes it (Sigma(tp*vol)/Sigma(vol), tp=(h+l+c)/3).
  * 9-EMA (orange line)         — alpha=2/(9+1)=0.2, seeded on the first bar, as the engine.
  * below-VWAP shading          — every bar where EMA<VWAP is shaded red, so you can
    verify the EMA was genuinely BELOW vwap for a sustained run before the cross.
  * entry triangle-up at (entry_time, entry_price)  — green win / red loss.
  * target level (dashed green) + stop level (dashed red) — the snapshotted geometry.
  * exit triangle-down at the actual exit, coloured by reason (target/stop/moc/time).

Title carries the selection context (run_below, stop_dist%, tightness, 1d/intraday
return, rvol) so the SETUP can be judged without opening the CSV.

Usage:
    dotnet run --project TradingEdge.VwapReclaim -c Release -- \
        --start-date 2023-01-01 --end-date 2023-12-31 -o /tmp/vw_chart.csv
    python scripts/visualization/vwap_reclaim_charts.py \
        --trips /tmp/vw_chart.csv --minute-dir data/minute_aggs \
        --output-dir data/charts/vwap_reclaim --limit 40
"""

from __future__ import annotations

import argparse
import csv
import os
from collections import defaultdict
from datetime import datetime, timezone

import duckdb
import plotly.graph_objects as go
from plotly.subplots import make_subplots

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))

SESSION_START_MIN = 9 * 60        # 09:30 ET — VWAP/EMA anchor (matches the engine's SessionStartMin)
SESSION_END_MIN = 16 * 60        # 16:00 ET — MOC
ENTRY_FLOOR_MIN = 9 * 60 + 45    # 09:45 ET — earliest entry
EMA_PERIOD = 9


def hhmm_to_min(s: str) -> int:
    h, m = s.split(':')
    return int(h) * 60 + int(m)


def load_trips(path):
    with open(path) as f:
        return list(csv.DictReader(f))


def load_session_bars(con, parquet_path, ticker, adj_ratio):
    """Load one ticker's 09:30..16:00 ET 1m bars, split-adjusted — the engine's view."""
    q = """
    WITH bars AS (
        SELECT
            CAST(date_part('hour', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT)*60
              + CAST(date_part('minute', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) AS et_min,
            open, high, low, close, volume
        FROM read_parquet(?) WHERE close > 0 AND ticker = ?)
    SELECT et_min, open, high, low, close, volume
    FROM bars WHERE et_min >= ? AND et_min <= ? ORDER BY et_min
    """
    rel = con.execute(q, [parquet_path, ticker, SESSION_START_MIN, SESSION_END_MIN])
    out = []
    for et_min, o, h, l, c, v in rel.fetchall():
        out.append({'et_min': et_min, 'open': o * adj_ratio, 'high': h * adj_ratio,
                    'low': l * adj_ratio, 'close': c * adj_ratio, 'volume': v})
    return out


def compute_vwap_ema(bars):
    """Session VWAP + 9-EMA per bar, EXACTLY as the engine folds them (post-fold values)."""
    vnum = vden = 0.0
    ema = None
    alpha = 2.0 / (EMA_PERIOD + 1.0)
    for b in bars:
        tp = (b['high'] + b['low'] + b['close']) / 3.0
        vnum += tp * b['volume']
        vden += b['volume']
        b['vwap'] = (vnum / vden) if vden > 0 else None
        ema = b['close'] if ema is None else (alpha * b['close'] + (1 - alpha) * ema)
        b['ema'] = ema
    return bars


def min_to_dt(et_min: int) -> datetime:
    return datetime(2000, 1, 1, et_min // 60, et_min % 60, tzinfo=timezone.utc)


def fnum(t, k, default=float('nan')):
    try:
        return float(t[k])
    except (KeyError, ValueError, TypeError):
        return default


def chart_day(con, symbol, date, trips, minute_dir, output_dir):
    adj_ratio = float(trips[0]['adj_ratio'])
    parquet_path = os.path.join(minute_dir, f'{date}.parquet')
    if not os.path.exists(parquet_path):
        return False, 'missing parquet'
    bars = load_session_bars(con, parquet_path, symbol, adj_ratio)
    if not bars:
        return False, 'no session bars'
    compute_vwap_ema(bars)

    x = [min_to_dt(b['et_min']) for b in bars]
    fig = make_subplots(rows=2, cols=1, shared_xaxes=True, vertical_spacing=0.04,
                        row_heights=[0.76, 0.24], subplot_titles=['Price (1m, adj) + VWAP + 9-EMA', 'Volume'])

    fig.add_trace(go.Candlestick(
        x=x, open=[b['open'] for b in bars], high=[b['high'] for b in bars],
        low=[b['low'] for b in bars], close=[b['close'] for b in bars], name='1m',
        increasing_line_color='#26a69a', decreasing_line_color='#ef5350', showlegend=False), row=1, col=1)

    # below-VWAP shading: shade each bar-span where EMA < VWAP (the weakness being reclaimed)
    for i, b in enumerate(bars):
        if b['ema'] is not None and b['vwap'] is not None and b['ema'] < b['vwap']:
            x0 = min_to_dt(b['et_min'])
            x1 = min_to_dt(bars[i + 1]['et_min']) if i + 1 < len(bars) else min_to_dt(b['et_min'] + 1)
            fig.add_vrect(x0=x0, x1=x1, fillcolor='#ef5350', opacity=0.06, layer='below',
                          line_width=0, row=1, col=1)

    # VWAP + EMA lines
    fig.add_trace(go.Scatter(x=x, y=[b['vwap'] for b in bars], mode='lines', name='VWAP',
                             line=dict(color='#7e57c2', width=1.6), showlegend=True), row=1, col=1)
    fig.add_trace(go.Scatter(x=x, y=[b['ema'] for b in bars], mode='lines', name='9-EMA',
                             line=dict(color='#ff9800', width=1.4), showlegend=True), row=1, col=1)

    vcolors = ['#26a69a' if b['close'] >= b['open'] else '#ef5350' for b in bars]
    fig.add_trace(go.Bar(x=x, y=[b['volume'] for b in bars], marker_color=vcolors,
                         marker_line_width=0, opacity=0.6, showlegend=False,
                         hovertemplate='%{x|%H:%M}<br>vol %{y:,.0f}<extra></extra>'), row=2, col=1)

    fig.add_vline(x=min_to_dt(ENTRY_FLOOR_MIN), line=dict(color='orange', width=1, dash='dash'), row=1, col=1)

    for t in trips:
        emin = hhmm_to_min(t['entry_time'])
        epx = fnum(t, 'entry_price')
        xmin = hhmm_to_min(t['exit_time'])
        xpx = fnum(t, 'exit_price')
        reason = t.get('exit_reason', '')
        win = fnum(t, 'net_pnl') > 0
        ecolor = '#1b7a1b' if win else '#b00020'
        rcolor = {'target': '#1b7a1b', 'stop': '#b00020', 'moc': '#555', 'time_stop': '#888'}.get(reason, '#555')

        # target + stop levels (reconstruct: entry=VWAP+ish; we stored stop_dist_pct and BreakoutRef=VWAP)
        vwap_at_entry = fnum(t, 'run_low_at_entry')       # = VWAP anchor stored as BreakoutRef
        stop_dist_pct = fnum(t, 'stop_dist_pct')
        stop_level = epx * (1 - stop_dist_pct) if stop_dist_pct == stop_dist_pct else None
        # target = VWAP + (VWAP - sessionLow) = VWAP + d; d = VWAP-sessionLow; but we only have VWAP + entry.
        # Recover d from stop: stop = VWAP - d/3  => d = 3*(VWAP - stop). target = VWAP + d.
        target_level = None
        if stop_level is not None and vwap_at_entry == vwap_at_entry:
            d = 3.0 * (vwap_at_entry - stop_level)
            target_level = vwap_at_entry + d

        span_x0, span_x1 = min_to_dt(emin), min_to_dt(xmin)
        if target_level is not None:
            fig.add_shape(type='line', x0=span_x0, x1=span_x1, y0=target_level, y1=target_level,
                          line=dict(color='#1b7a1b', width=1, dash='dash'), row=1, col=1)
        if stop_level is not None:
            fig.add_shape(type='line', x0=span_x0, x1=span_x1, y0=stop_level, y1=stop_level,
                          line=dict(color='#b00020', width=1, dash='dash'), row=1, col=1)

        fig.add_trace(go.Scatter(
            x=[span_x0], y=[epx], mode='markers', marker=dict(symbol='triangle-up', size=13, color=ecolor,
            line=dict(color='black', width=1)), showlegend=False,
            hovertemplate=(f"ENTRY {t['entry_time']}  px %{{y:.4f}}<br>"
                           f"run_below {t.get('run_below_vwap','?')} bars  stop_dist {stop_dist_pct*100:.2f}%<br>"
                           f"tight {fnum(t,'intraday_tightness_at_entry'):.2f}  "
                           f"1d {fnum(t,'chg_1d')*100:+.1f}%<extra></extra>")), row=1, col=1)
        fig.add_trace(go.Scatter(x=[span_x0, span_x1], y=[epx, xpx], mode='lines',
                      line=dict(color=ecolor, width=1, dash='dot'), showlegend=False, hoverinfo='skip'), row=1, col=1)
        fig.add_trace(go.Scatter(
            x=[span_x1], y=[xpx], mode='markers', marker=dict(symbol='triangle-down', size=13, color=rcolor,
            line=dict(color='black', width=1)), showlegend=False,
            hovertemplate=(f"EXIT {t['exit_time']} [{reason}]  px %{{y:.4f}}<br>"
                           f"pnl {fnum(t,'net_pnl'):,.0f}<extra></extra>")), row=1, col=1)

    t0 = trips[0]
    n = len(trips)
    pnl = sum(fnum(t, 'net_pnl') for t in trips)
    title = (f"{symbol} {date} — {n} reclaim trip(s)  |  pnl {pnl:,.0f}<br>"
             f"<sup>rb {t0.get('run_below_vwap','?')}  stop_dist {fnum(t0,'stop_dist_pct')*100:.2f}%  "
             f"tight {fnum(t0,'intraday_tightness_at_entry'):.2f}  "
             f"1d {fnum(t0,'chg_1d')*100:+.1f}%  rvol {fnum(t0,'rvol'):.1f}  "
             f"exit [{t0.get('exit_reason','')}]</sup>")

    fig.update_layout(title=title, height=780, width=1400, hovermode='closest',
                      xaxis_rangeslider_visible=False, showlegend=True,
                      legend=dict(orientation='h', yanchor='bottom', y=1.02, x=0), margin=dict(t=90))
    fig.update_xaxes(tickformat='%H:%M', row=2, col=1)
    fig.update_yaxes(title_text='Price', row=1, col=1)
    fig.update_yaxes(title_text='Volume', row=2, col=1)

    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'chart_controls.js')) as f:
        post_script = f.read()
    out_html = os.path.join(output_dir, f'{symbol}_{date}.html')
    fig.write_html(out_html, config={'scrollZoom': True, 'displayModeBar': True}, post_script=post_script)
    if os.environ.get('VW_PNG'):
        try:
            fig.write_image(os.path.join(output_dir, f'{symbol}_{date}.png'), width=1400, height=780, scale=1)
        except Exception as e:
            print(f'   png export failed: {e}')
    return True, 'rendered'


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--trips', default='/tmp/vw_chart.csv')
    ap.add_argument('--minute-dir', default='data/minute_aggs')
    ap.add_argument('--output-dir', default='data/charts/vwap_reclaim')
    ap.add_argument('--limit', type=int, default=40, help='max (symbol,date) charts to render')
    ap.add_argument('--rb-band', type=int, nargs=2, default=[0, 100000], metavar=('LO', 'HI'),
                    help='run_below_vwap band [LO,HI] inclusive (default all)')
    ap.add_argument('--et-band', type=int, nargs=2, default=[0, 1440], metavar=('LO', 'HI'),
                    help='entry ET-minute window [LO,HI) (600=10:00, 810=13:30)')
    args = ap.parse_args()

    minute_dir = args.minute_dir if os.path.isabs(args.minute_dir) else os.path.join(ROOT, args.minute_dir)
    output_dir = args.output_dir if os.path.isabs(args.output_dir) else os.path.join(ROOT, args.output_dir)
    os.makedirs(output_dir, exist_ok=True)

    rows = load_trips(args.trips)
    # Simple Python-side filters (avoids DuckDB TIME<->string key mismatch). Supported keys:
    #   rb_lo / rb_hi (run_below_vwap band), et_lo / et_hi (entry ET-minute window).
    rb_lo, rb_hi = args.rb_band
    et_lo, et_hi = args.et_band

    def keep(r):
        rb = int(float(r.get('run_below_vwap', 0) or 0))
        em = hhmm_to_min(r['entry_time'])
        return rb_lo <= rb <= rb_hi and et_lo <= em < et_hi
    rows = [r for r in rows if keep(r)]

    by_day = defaultdict(list)
    for r in rows:
        by_day[(r['symbol'], r['trade_date'])].append(r)

    con = duckdb.connect(database=':memory:')
    rendered = failed = 0
    for (symbol, date), trips in sorted(by_day.items()):
        if rendered >= args.limit:
            break
        try:
            ok, status = chart_day(con, symbol, date, trips, minute_dir, output_dir)
        except Exception as e:
            ok, status = False, f'error: {e}'
        if ok:
            print(f'  {symbol} {date} ({len(trips)} trip) -> {status}')
            rendered += 1
        else:
            print(f'  {symbol} {date} -> FAIL {status}')
            failed += 1
    print(f'\nDone. rendered={rendered} failed={failed}  charts in {output_dir}')


if __name__ == '__main__':
    main()
