"""Render 1m candlestick charts of MaxFlyer intraday trips, with entries/exits marked.

For each (symbol, trade_date) that produced a trip, this loads THAT day's
`data/minute_aggs/{date}.parquet` — the exact source the MaxFlyer engine trades
off — split-adjusts it by the trip's `adj_ratio` (so prices match the engine's
adjusted scale), and renders the 08:30->16:00 ET session (the engine's
SessionStartMin..MocMin) as a 1m candlestick + volume panel. Every trip on that
day is overlaid:

  * entry ▲ at (entry_time, entry_price); colour = win/loss of the HOLD-TO-MOC trip.
  * exit  ▼ — TWO of them per trip:
      - the no-stop (hold-to-MOC) exit  (solid marker + dashed connector)
      - where the 2-bar-low STOP would have fired (hollow marker), if different.
    so the stop's per-trade effect is visible at a glance.
  * run_high_at_entry  — dotted horizontal line (the breakout level the entry cleared).
  * stop_lo            — dashed horizontal line (the 2-bar local low / stop level).
  * 09:30 RTH-open and 09:35 entry-floor vertical guides; 08:30-09:30 premarket shaded.

The title carries the daily Gate-1 snapshots (gap%, daily tightness, ATR%, premkt
vol as a fraction-of-avg) so the SELECTION can be judged against the thesis without
opening the CSV — the whole point of this pass (cf. the crypto consolidation-breakout
lesson: a bad PF aggregate hides whether the filters are doing what you think).

Takes the two trips CSVs the engine writes (no-stop run + --intraday-stop run); they
share entry keys exactly (same candidates, same entries — only the exit differs), so
they're joined on (symbol, trade_date, entry_time).

Usage:
    # 1. produce both trip CSVs from the engine:
    dotnet run --project TradingEdge.MaxFlyer -c Release -- -o /tmp/maxflyer_trips.csv
    dotnet run --project TradingEdge.MaxFlyer -c Release -- --intraday-stop -o /tmp/maxflyer_stop.csv
    # 2. render:
    python scripts/visualization/maxflyer_charts.py \
        --trips /tmp/maxflyer_trips.csv --stop-trips /tmp/maxflyer_stop.csv \
        --minute-dir data/minute_aggs --output-dir data/charts/maxflyer
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

SESSION_START_MIN = 8 * 60 + 30   # 08:30 ET — engine session start
SESSION_END_MIN = 16 * 60         # 16:00 ET — MOC
PREMKT_END_MIN = 9 * 60 + 30      # 09:30 ET — RTH open
ENTRY_FLOOR_MIN = 9 * 60 + 35     # 09:35 ET — earliest entry


def hhmm_to_min(s: str) -> int:
    h, m = s.split(':')
    return int(h) * 60 + int(m)


def load_trips(path):
    """Read a trips CSV into a list of dict rows with the numeric fields parsed."""
    rows = []
    with open(path) as f:
        for r in csv.DictReader(f):
            rows.append(r)
    return rows


def load_session_bars(con, parquet_path, ticker, adj_ratio):
    """Load one ticker's 08:30..16:00 ET 1m bars from a date parquet, split-adjusted.

    ET conversion ported from scripts/equity/intraday_checkpoints.py (lines 84-90),
    identical to the engine's MinuteEmitter SQL, so the chart sees what the engine saw.
    """
    q = """
    WITH bars AS (
        SELECT
            CAST(date_part('hour', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT)*60
              + CAST(date_part('minute', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) AS et_min,
            open, high, low, close, volume
        FROM read_parquet(?) WHERE close > 0 AND ticker = ?)
    SELECT et_min, open, high, low, close, volume
    FROM bars WHERE et_min >= ? AND et_min <= ?
    ORDER BY et_min
    """
    rel = con.execute(q, [parquet_path, ticker, SESSION_START_MIN, SESSION_END_MIN])
    out = []
    for et_min, o, h, l, c, v in rel.fetchall():
        out.append({
            'et_min': et_min,
            'open': o * adj_ratio, 'high': h * adj_ratio,
            'low': l * adj_ratio, 'close': c * adj_ratio,
            'volume': v,
        })
    return out


def min_to_dt(et_min: int) -> datetime:
    """A nominal datetime for the x-axis (date is irrelevant; only the time-of-day matters)."""
    return datetime(2000, 1, 1, et_min // 60, et_min % 60, tzinfo=timezone.utc)


def chart_day(con, symbol, date, trips, minute_dir, output_dir):
    """Render one (symbol, date) chart with all its trips marked. trips: list of merged dicts."""
    adj_ratio = float(trips[0]['adj_ratio'])
    parquet_path = os.path.join(minute_dir, f'{date}.parquet')
    if not os.path.exists(parquet_path):
        return False, 'missing parquet'

    bars = load_session_bars(con, parquet_path, symbol, adj_ratio)
    if not bars:
        return False, 'no session bars'

    x = [min_to_dt(b['et_min']) for b in bars]
    fig = make_subplots(rows=2, cols=1, shared_xaxes=True, vertical_spacing=0.04,
                        row_heights=[0.74, 0.26], subplot_titles=['Price (1m, adj)', 'Volume'])

    fig.add_trace(go.Candlestick(
        x=x,
        open=[b['open'] for b in bars], high=[b['high'] for b in bars],
        low=[b['low'] for b in bars], close=[b['close'] for b in bars],
        name='1m', increasing_line_color='#26a69a', decreasing_line_color='#ef5350',
        showlegend=False), row=1, col=1)

    # volume, coloured up/down like the candles
    vcolors = ['#26a69a' if b['close'] >= b['open'] else '#ef5350' for b in bars]
    fig.add_trace(go.Bar(x=x, y=[b['volume'] for b in bars], marker_color=vcolors,
                         marker_line_width=0, opacity=0.6, showlegend=False,
                         hovertemplate='%{x|%H:%M}<br>vol %{y:,.0f}<extra></extra>'), row=2, col=1)

    # premarket shading (08:30-09:30) + RTH-open / entry-floor guides
    fig.add_vrect(x0=min_to_dt(SESSION_START_MIN), x1=min_to_dt(PREMKT_END_MIN),
                  fillcolor='lightblue', opacity=0.15, layer='below', line_width=0, row=1, col=1)
    fig.add_vrect(x0=min_to_dt(SESSION_START_MIN), x1=min_to_dt(PREMKT_END_MIN),
                  fillcolor='lightblue', opacity=0.15, layer='below', line_width=0, row=2, col=1)
    for m, dash, color in [(PREMKT_END_MIN, 'dot', 'gray'), (ENTRY_FLOOR_MIN, 'dash', 'orange')]:
        fig.add_vline(x=min_to_dt(m), line=dict(color=color, width=1, dash=dash), row=1, col=1)

    # ---- mark each trip ----
    for t in trips:
        emin = hhmm_to_min(t['entry_time'])
        epx = float(t['entry_price'])
        run_hi = float(t['run_high_at_entry'])
        stop_lo = float(t['stop_lo'])
        moc_min = hhmm_to_min(t['moc_exit_time'])
        moc_px = float(t['moc_exit_price'])
        win = float(t['moc_net_pnl']) > 0
        ecolor = '#1b7a1b' if win else '#b00020'

        # breakout level the entry cleared + the 2-bar stop level (per-trip horizontals,
        # drawn only across the holding span so overlapping trips stay legible)
        span_x0, span_x1 = min_to_dt(emin), min_to_dt(moc_min)
        fig.add_shape(type='line', x0=span_x0, x1=span_x1, y0=run_hi, y1=run_hi,
                      line=dict(color='#888', width=1, dash='dot'), row=1, col=1)
        fig.add_shape(type='line', x0=span_x0, x1=span_x1, y0=stop_lo, y1=stop_lo,
                      line=dict(color='#c79100', width=1, dash='dash'), row=1, col=1)

        # entry marker
        fig.add_trace(go.Scatter(
            x=[min_to_dt(emin)], y=[epx], mode='markers', name='entry',
            marker=dict(symbol='triangle-up', size=13, color=ecolor,
                        line=dict(color='black', width=1)),
            showlegend=False,
            hovertemplate=(f"ENTRY {t['entry_time']}<br>px %{{y:.4f}}<br>"
                           f"run_hi {run_hi:.4f}  stop_lo {stop_lo:.4f}<br>"
                           f"intraday tight {float(t['intraday_tightness_at_entry']):.2f}<extra></extra>")),
            row=1, col=1)

        # hold-to-MOC exit (solid ▼) + dashed connector entry->moc
        fig.add_trace(go.Scatter(
            x=[span_x0, span_x1], y=[epx, moc_px], mode='lines',
            line=dict(color=ecolor, width=1, dash='dash'), showlegend=False,
            hoverinfo='skip'), row=1, col=1)
        fig.add_trace(go.Scatter(
            x=[min_to_dt(moc_min)], y=[moc_px], mode='markers', name='moc',
            marker=dict(symbol='triangle-down', size=13, color=ecolor,
                        line=dict(color='black', width=1)),
            showlegend=False,
            hovertemplate=(f"MOC EXIT {t['moc_exit_time']}<br>px %{{y:.4f}}<br>"
                           f"pnl {float(t['moc_net_pnl']):,.0f}<extra></extra>")),
            row=1, col=1)

        # stop exit (hollow ▽), only if the stop run actually stopped out (different exit)
        if t.get('stop_exit_reason') == 'intraday_stop':
            smin = hhmm_to_min(t['stop_exit_time'])
            spx = float(t['stop_exit_price'])
            fig.add_trace(go.Scatter(
                x=[min_to_dt(smin)], y=[spx], mode='markers', name='stop',
                marker=dict(symbol='triangle-down-open', size=15, color='#c79100',
                            line=dict(color='#c79100', width=2)),
                showlegend=False,
                hovertemplate=(f"STOP EXIT {t['stop_exit_time']}<br>px %{{y:.4f}}<br>"
                               f"pnl {float(t['stop_net_pnl']):,.0f}<extra></extra>")),
                row=1, col=1)

    # ---- title carrying the daily Gate-1 selection snapshots (judge the filter) ----
    t0 = trips[0]
    premkt_vol = int(t0['premkt_vol'])
    adv = float(t0['avg_dollar_volume_4w'])
    # premkt vol as a fraction of a normal full day's *share* volume isn't directly in
    # the CSV (adv is dollar volume); show absolute premkt vol + the gap + daily filters.
    n = len(trips)
    moc_pnl = sum(float(t['moc_net_pnl']) for t in trips)
    stop_pnl = sum(float(t['stop_net_pnl']) for t in trips)
    title = (f"{symbol} {date} — gap {float(t0['gap_pct'])*100:.0f}%  "
             f"daily tight {float(t0['daily_tightness']):.2f}  atr% {float(t0['daily_atr_pct']):.3f}  "
             f"premkt_vol {premkt_vol:,}<br>"
             f"<sup>{n} trip(s)  |  hold-to-MOC pnl {moc_pnl:,.0f}  |  with-stop pnl {stop_pnl:,.0f}  "
             f"|  pct_52w {float(t0['pct_52w'])*100:+.1f}%</sup>")

    fig.update_layout(title=title, height=760, width=1400, hovermode='closest',
                      xaxis_rangeslider_visible=False, showlegend=False,
                      margin=dict(t=80))
    fig.update_xaxes(tickformat='%H:%M', row=2, col=1)
    fig.update_yaxes(title_text='Price', row=1, col=1)
    fig.update_yaxes(title_text='Volume', row=2, col=1)

    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'chart_controls.js')) as f:
        post_script = f.read()

    out_html = os.path.join(output_dir, f'{symbol}_{date}.html')
    fig.write_html(out_html, config={'scrollZoom': True, 'displayModeBar': True},
                   post_script=post_script)
    return True, 'rendered'


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--trips', default='/tmp/maxflyer_trips.csv',
                    help='hold-to-MOC trips CSV (no --intraday-stop)')
    ap.add_argument('--stop-trips', default='/tmp/maxflyer_stop.csv',
                    help='trips CSV from the --intraday-stop run (same entries, stop exits)')
    ap.add_argument('--minute-dir', default='data/minute_aggs')
    ap.add_argument('--output-dir', default='data/charts/maxflyer')
    args = ap.parse_args()

    minute_dir = args.minute_dir if os.path.isabs(args.minute_dir) else os.path.join(ROOT, args.minute_dir)
    output_dir = args.output_dir if os.path.isabs(args.output_dir) else os.path.join(ROOT, args.output_dir)
    os.makedirs(output_dir, exist_ok=True)

    moc_rows = load_trips(args.trips)
    stop_rows = load_trips(args.stop_trips)
    # key on (symbol, trade_date, entry_time): same candidates+entries across runs.
    stop_by_key = {(r['symbol'], r['trade_date'], r['entry_time']): r for r in stop_rows}

    # merge: each trip carries its hold-to-MOC exit AND its stop exit, then group by day.
    by_day = defaultdict(list)
    for r in moc_rows:
        key = (r['symbol'], r['trade_date'], r['entry_time'])
        s = stop_by_key.get(key)
        merged = dict(r)
        merged['moc_exit_time'] = r['exit_time']
        merged['moc_exit_price'] = r['exit_price']
        merged['moc_net_pnl'] = r['net_pnl']
        if s is not None:
            merged['stop_exit_reason'] = s['exit_reason']
            merged['stop_exit_time'] = s['exit_time']
            merged['stop_exit_price'] = s['exit_price']
            merged['stop_net_pnl'] = s['net_pnl']
        else:
            merged['stop_exit_reason'] = ''
            merged['stop_net_pnl'] = r['net_pnl']
        by_day[(r['symbol'], r['trade_date'])].append(merged)

    con = duckdb.connect(database=':memory:')
    rendered = failed = 0
    for (symbol, date), trips in sorted(by_day.items()):
        try:
            ok, status = chart_day(con, symbol, date, trips, minute_dir, output_dir)
        except Exception as e:
            ok, status = False, f'error: {e}'
        tag = f'{symbol} {date} ({len(trips)} trip)'
        if ok:
            print(f'  {tag} -> {status}')
            rendered += 1
        else:
            print(f'  {tag} -> FAIL {status}')
            failed += 1

    print(f'\nDone. rendered={rendered} failed={failed}  charts in {output_dir}')


if __name__ == '__main__':
    main()
