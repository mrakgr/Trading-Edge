"""Render 1m candlestick charts for the morning-panic scan (Tim Sykes setup).

Input = the panic-scan CSV (one row per (symbol, trade_date) that cleared the
scan: a >=500% prior-7d run-up followed by a first-hour flush >=50% below the
prior close, with the off-the-open glitch-guard). For each row this loads THAT
day's `data/minute_aggs/{date}.parquet`, ET-converts + split-adjusts by the
row's `adj_ratio` (so prices sit in the same adjusted scale as the daily
`prev_close_adj`), and draws the whole 08:30->16:00 ET session as a 1m
candlestick + volume panel so the V-reversal (or lack of one) is visible by eye.

Overlaid, so the pattern can be judged without opening the CSV:
  * prior close        — dashed horizontal line (the reference the flush is measured from)
  * morning-window low  — dotted horizontal line (the 09:30-10:30 low that triggered)
  * 09:30 RTH open + 10:30 morning-window-end vertical guides; 08:30-09:30 premkt shaded
Title carries the 7d run-up %, the two flush legs, and morning volume.

This reuses the 1m load/ET/adjust machinery from maxflyer_charts.py but is
driven by the scan CSV directly (no trip entries/exits — these are candidates
to STUDY, not backtested trades).

Usage:
    python scripts/visualization/morning_panic_charts.py \
        --scan /tmp/.../morning_panic_17.csv \
        --minute-dir data/minute_aggs --output-dir data/charts/morning_panic
"""

from __future__ import annotations

import argparse
import csv
import os
from datetime import datetime, timezone

import duckdb
import plotly.graph_objects as go
from plotly.subplots import make_subplots

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))

SESSION_START_MIN = 8 * 60 + 30   # 08:30 ET
SESSION_END_MIN = 16 * 60         # 16:00 ET — MOC
PREMKT_END_MIN = 9 * 60 + 30      # 09:30 ET — RTH open
MORNING_END_MIN = 10 * 60 + 30    # 10:30 ET — end of the panic-scan window


def load_scan(path):
    with open(path) as f:
        return list(csv.DictReader(f))


def load_session_bars(con, parquet_path, ticker, adj_ratio):
    """08:30..16:00 ET 1m bars from a date parquet, split-adjusted to the daily scale.

    ET conversion identical to maxflyer_charts.py / the engine's MinuteEmitter SQL.
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
    """Nominal datetime for the x-axis (only the time-of-day matters)."""
    return datetime(2000, 1, 1, et_min // 60, et_min % 60, tzinfo=timezone.utc)


def chart_day(con, row, minute_dir, output_dir):
    symbol = row['symbol']
    date = row['trade_date']
    adj_ratio = float(row['adj_ratio'])
    prev_close = float(row['prev_close_adj'])
    m_low = float(row['m_low_adj'])
    runup_7d = float(row['runup_7d'])
    flush_pclose = float(row['flush_pclose'])
    flush_open = float(row['flush_open'])
    m_vol = int(row['m_vol'])

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

    vcolors = ['#26a69a' if b['close'] >= b['open'] else '#ef5350' for b in bars]
    fig.add_trace(go.Bar(x=x, y=[b['volume'] for b in bars], marker_color=vcolors,
                         marker_line_width=0, opacity=0.6, showlegend=False,
                         hovertemplate='%{x|%H:%M}<br>vol %{y:,.0f}<extra></extra>'), row=2, col=1)

    # premarket shading (08:30-09:30) + RTH-open / morning-window-end guides
    for r in (1, 2):
        fig.add_vrect(x0=min_to_dt(SESSION_START_MIN), x1=min_to_dt(PREMKT_END_MIN),
                      fillcolor='lightblue', opacity=0.15, layer='below', line_width=0, row=r, col=1)
    for m, dash, color in [(PREMKT_END_MIN, 'dot', 'gray'), (MORNING_END_MIN, 'dash', 'orange')]:
        fig.add_vline(x=min_to_dt(m), line=dict(color=color, width=1, dash=dash), row=1, col=1)

    # prior close (flush reference) + morning-window low (the trigger level)
    fig.add_hline(y=prev_close, line=dict(color='#1f77b4', width=1, dash='dash'),
                  annotation_text=f'prev close {prev_close:.2f}', annotation_position='top left',
                  row=1, col=1)
    fig.add_hline(y=m_low, line=dict(color='#c79100', width=1, dash='dot'),
                  annotation_text=f'AM low {m_low:.2f}', annotation_position='bottom left',
                  row=1, col=1)

    title = (f"{symbol} {date} — MORNING PANIC<br>"
             f"<sup>7d run-up {runup_7d*100:+.0f}%  |  AM-low vs prev-close {flush_pclose*100:.0f}%  "
             f"|  AM-low vs open {flush_open*100:.0f}%  |  AM vol {m_vol:,}</sup>")

    fig.update_layout(title=title, height=760, width=1400, hovermode='closest',
                      xaxis_rangeslider_visible=False, showlegend=False, margin=dict(t=80))
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
    ap.add_argument('--scan', required=True, help='morning-panic scan CSV')
    ap.add_argument('--minute-dir', default='data/minute_aggs')
    ap.add_argument('--output-dir', default='data/charts/morning_panic')
    args = ap.parse_args()

    minute_dir = args.minute_dir if os.path.isabs(args.minute_dir) else os.path.join(ROOT, args.minute_dir)
    output_dir = args.output_dir if os.path.isabs(args.output_dir) else os.path.join(ROOT, args.output_dir)
    os.makedirs(output_dir, exist_ok=True)

    rows = load_scan(args.scan)
    con = duckdb.connect(database=':memory:')
    rendered = failed = 0
    for row in rows:
        tag = f"{row['symbol']} {row['trade_date']}"
        try:
            ok, status = chart_day(con, row, minute_dir, output_dir)
        except Exception as e:
            ok, status = False, f'error: {e}'
        if ok:
            print(f'  {tag} -> {status}')
            rendered += 1
        else:
            print(f'  {tag} -> FAIL {status}')
            failed += 1

    print(f'\nDone. rendered={rendered} failed={failed}  charts in {output_dir}')


if __name__ == '__main__':
    main()
