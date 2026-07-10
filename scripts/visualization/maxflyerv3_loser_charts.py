"""Render 1m charts of the WORST MaxFlyerV3 short losers, to design a stop.

The V3 short pop-fade holds to MOC with NO stop, so its losses are short SQUEEZES
that ran all day. This pass charts the worst-N losers (by net P&L) from the A-book
(brv20d>=100 & intraday ATR%>=0.03) so we can SEE the loss trajectory: does the
squeeze rip fast (a tight % stop catches it cheaply) or grind up slowly (needs a
wider / time stop)?

For each losing (symbol, trade_date) it loads that day's data/minute_aggs/{date}.parquet
(the exact source the engine trades off), split-adjusts by the trip's adj_ratio, and
renders the 08:30->16:00 ET session as 1m candles + a volume panel. Overlaid:

  * entry  ▼ (short) at (entry_time, entry_price)  — red.
  * MOC exit ● at 16:00 (exit_price)               — where the no-stop trade actually closed.
  * entry level                 — solid horizontal (the short's cost basis).
  * candidate STOP levels        — dashed horizontals at entry*(1+f) for f in STOP_FRACS
                                   (a short stops out when price RISES through these).
    The first bar whose HIGH pierces each level is annotated with the minute + the
    would-be loss %, so the stop's timing & cost is visible per chart.
  * intraday session HIGH        — dotted horizontal (the worst-case adverse excursion).

Reads ONE trips CSV (the ungated V3 run); the A-book filter + loser ranking are done
here in DuckDB against mr_candidate (for brv20d), so no second engine run is needed.

Usage:
    dotnet run --project TradingEdge.MaxFlyerV3 -c Release -- -o /tmp/mfv3_short_ungated.csv
    python scripts/visualization/maxflyerv3_loser_charts.py \
        --trips /tmp/mfv3_short_ungated.csv --db data/trading.db \
        --minute-dir data/minute_aggs --output-dir data/charts/mfv3_losers --worst 25
"""

from __future__ import annotations

import argparse
import os
from datetime import datetime, timezone

import duckdb
import plotly.graph_objects as go
from plotly.subplots import make_subplots

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))

SESSION_START_MIN = 8 * 60 + 30   # 08:30 ET
SESSION_END_MIN = 16 * 60         # 16:00 ET
PREMKT_END_MIN = 9 * 60 + 30      # 09:30 ET RTH open
ENTRY_FLOOR_MIN = 9 * 60 + 45     # 09:45 ET earliest entry (V3 default)

# Candidate catastrophe-stop levels: a short stops when price rises this fraction above entry.
STOP_FRACS = [0.15, 0.25, 0.40]
STOP_COLORS = {0.15: '#f9a825', 0.25: '#ef6c00', 0.40: '#b71c1c'}


def hhmm_to_min(s) -> int:
    if hasattr(s, 'hour'):          # datetime.time (DuckDB parsed the CSV column)
        return s.hour * 60 + s.minute
    h, m = str(s).split(':')[:2]
    return int(h) * 60 + int(m)


def min_to_dt(et_min: int) -> datetime:
    return datetime(2000, 1, 1, et_min // 60, et_min % 60, tzinfo=timezone.utc)


def select_losers(con, trips_csv, worst):
    """A-book filter + worst-N losers by net P&L, joined to mr_candidate for brv20d."""
    q = """
    WITH t AS (
        SELECT r.symbol, r.trade_date, r.adj_ratio, r.entry_time, r.entry_price,
               r.exit_time, r.exit_price, r.ret_moc, r.net_pnl, r.qty,
               r.chg_1d, r.intraday_atr_pct_at_entry AS iatr,
               r.breakout_bar_vol / NULLIF(mc.avgvol20*mc.adj_ratio/390.0,0) AS brv20d
        FROM read_csv_auto(?) r
        JOIN mr_candidate mc ON mc.ticker=r.symbol AND mc.date=r.trade_date
        WHERE r.intraday_atr_pct_at_entry >= 0.03 AND mc.avgvol20 > 0 AND mc.adj_ratio > 0
    )
    SELECT * FROM t WHERE brv20d >= 100 ORDER BY net_pnl ASC LIMIT ?
    """
    cols = [d[0] for d in con.execute(q, [trips_csv, worst]).description]
    return [dict(zip(cols, row)) for row in con.execute(q, [trips_csv, worst]).fetchall()]


def load_session_bars(con, parquet_path, ticker, adj_ratio):
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
    return [{'et_min': em, 'open': o*adj_ratio, 'high': h*adj_ratio,
             'low': l*adj_ratio, 'close': c*adj_ratio, 'volume': v}
            for em, o, h, l, c, v in rel.fetchall()]


def first_pierce(bars, entry_min, level):
    """First bar AT/AFTER entry whose HIGH reaches `level` (short stop trigger). Returns (et_min, close) or None."""
    for b in bars:
        if b['et_min'] >= entry_min and b['high'] >= level:
            return b['et_min'], b['close']
    return None


def render(con, t, minute_dir, out_dir):
    symbol = t['symbol']
    date = str(t['trade_date'])
    adj_ratio = float(t['adj_ratio'])
    parquet = os.path.join(minute_dir, f'{date}.parquet')
    if not os.path.exists(parquet):
        print(f'  skip {symbol} {date}: no parquet')
        return
    bars = load_session_bars(con, parquet, symbol, adj_ratio)
    if not bars:
        print(f'  skip {symbol} {date}: no bars')
        return

    entry_min = hhmm_to_min(t['entry_time'])
    entry_px = float(t['entry_price'])
    exit_min = hhmm_to_min(t['exit_time'])
    exit_px = float(t['exit_price'])
    ret_pct = 100.0 * float(t['ret_moc'])
    net = float(t['net_pnl'])
    sess_high = max(b['high'] for b in bars)

    x = [min_to_dt(b['et_min']) for b in bars]
    fig = make_subplots(rows=2, cols=1, shared_xaxes=True, vertical_spacing=0.03,
                        row_heights=[0.76, 0.24], subplot_titles=['Price (1m, adj)', 'Volume'])
    fig.add_trace(go.Candlestick(x=x,
        open=[b['open'] for b in bars], high=[b['high'] for b in bars],
        low=[b['low'] for b in bars], close=[b['close'] for b in bars],
        name='1m', increasing_line_color='#26a69a', decreasing_line_color='#ef5350'), row=1, col=1)
    vcolors = ['#26a69a' if b['close'] >= b['open'] else '#ef5350' for b in bars]
    fig.add_trace(go.Bar(x=x, y=[b['volume'] for b in bars], marker_color=vcolors,
                         name='vol', showlegend=False), row=2, col=1)

    span0, span1 = x[0], x[-1]
    # entry level (short cost basis) — solid.
    fig.add_shape(type='line', x0=span0, x1=span1, y0=entry_px, y1=entry_px,
                  line=dict(color='#1e88e5', width=1.4), row=1, col=1)
    # session high — dotted (worst adverse excursion).
    fig.add_shape(type='line', x0=span0, x1=span1, y0=sess_high, y1=sess_high,
                  line=dict(color='#6a1b9a', width=1, dash='dot'), row=1, col=1)

    # candidate stop levels + first-pierce annotation.
    stop_notes = []
    for f in STOP_FRACS:
        lvl = entry_px * (1.0 + f)
        fig.add_shape(type='line', x0=span0, x1=span1, y0=lvl, y1=lvl,
                      line=dict(color=STOP_COLORS[f], width=1, dash='dash'), row=1, col=1)
        p = first_pierce(bars, entry_min, lvl)
        if p:
            pm, _ = p
            stop_notes.append(f'+{int(f*100)}%@{pm//60:02d}:{pm%60:02d}')
        else:
            stop_notes.append(f'+{int(f*100)}%:never')

    # entry marker (short = ▼ red) and MOC exit (●).
    fig.add_trace(go.Scatter(x=[min_to_dt(entry_min)], y=[entry_px], mode='markers',
        marker=dict(symbol='triangle-down', size=13, color='#d32f2f', line=dict(color='#000', width=1)),
        name='SHORT entry',
        hovertemplate=f"SHORT {t['entry_time']}<br>px {entry_px:.4f}<br>brv20d {float(t['brv20d']):.0f}  1d +{100*float(t['chg_1d']):.0f}%<extra></extra>"),
        row=1, col=1)
    fig.add_trace(go.Scatter(x=[min_to_dt(exit_min)], y=[exit_px], mode='markers',
        marker=dict(symbol='circle', size=11, color='#000'),
        name='MOC exit',
        hovertemplate=f"MOC {t['exit_time']}<br>px {exit_px:.4f}<br>ret {ret_pct:+.0f}%  net {net:,.0f}<extra></extra>"),
        row=1, col=1)

    for gm, lbl in [(PREMKT_END_MIN, '09:30'), (ENTRY_FLOOR_MIN, '09:45')]:
        fig.add_vline(x=min_to_dt(gm), line=dict(color='#999', width=0.8, dash='dot'), row=1, col=1)

    title = (f"{symbol} {date} — SHORT ret {ret_pct:+.0f}%  net {net:,.0f}  | "
             f"entry {t['entry_time']} @{entry_px:.3f}  brv20d {float(t['brv20d']):.0f}  "
             f"1d +{100*float(t['chg_1d']):.0f}%  ATR% {float(t['iatr']):.3f}<br>"
             f"<sub>stop pierce: {'  '.join(stop_notes)}   |   sess-high {sess_high:.3f} "
             f"({100*(sess_high/entry_px-1):+.0f}% adverse)</sub>")
    fig.update_layout(title=title, template='plotly_white', height=760, width=1180,
                      xaxis_rangeslider_visible=False, showlegend=True,
                      legend=dict(orientation='h', yanchor='bottom', y=1.02, x=0))
    fig.update_yaxes(title_text='price', row=1, col=1)
    fig.update_yaxes(title_text='vol', row=2, col=1)

    os.makedirs(out_dir, exist_ok=True)
    rank_px = f"{abs(net):08.0f}"
    fn = os.path.join(out_dir, f'{rank_px}_{symbol}_{date}.html')
    post_script = open(os.path.join(ROOT, 'scripts/visualization/chart_controls.js')).read()
    fig.write_html(fn, include_plotlyjs='cdn',
                   config={'scrollZoom': True, 'displayModeBar': True}, post_script=post_script)
    print(f'  {symbol} {date}  net {net:>10,.0f}  ret {ret_pct:+.0f}%  ->  {fn}')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--trips', required=True)
    ap.add_argument('--db', default=os.path.join(ROOT, 'data/trading.db'))
    ap.add_argument('--minute-dir', default=os.path.join(ROOT, 'data/minute_aggs'))
    ap.add_argument('--output-dir', default=os.path.join(ROOT, 'data/charts/mfv3_losers'))
    ap.add_argument('--worst', type=int, default=25)
    args = ap.parse_args()

    con = duckdb.connect(args.db, read_only=True)
    losers = select_losers(con, args.trips, args.worst)
    print(f'rendering {len(losers)} worst losers -> {args.output_dir}')
    for t in losers:
        render(con, t, args.minute_dir, args.output_dir)


if __name__ == '__main__':
    main()
