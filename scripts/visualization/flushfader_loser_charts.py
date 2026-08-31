"""Render 1-SECOND charts of FlushFader trips, to design a knife-avoidance feature.

Built for the S43ap question: the deep-flush cell (dlo2m < -9%) is strongly
positive in every year except 2022, and 2022's whole loss is essentially ONE
ticker-day (ENSV 2022-03-08, -56% of a -59% year). Rather than fit a rule to
that, look at the tape and see what a knife looks like while it is falling.

For each (symbol, trade_date) it loads that day's data/intraday_1s_slim/{date}.parquet
-- the exact 1s tape the engine trades off -- and renders the RTH session as a
1s vwap line + a volume panel. Overlaid:

  * signal  v   at (signal_sec, signal_vwap)      -- the new 20m low that armed it
  * entry   o   at (entry_sec, entry_px)          -- filled NEXT bar, so one bar later
  * exit    x   at (exit_sec, exit_px)            -- coloured by outcome
  * a line joining each entry to its exit         -- green if it made money, red if not
  * the ROLLING 5m MAX (the exit target)          -- THE POINT: this level ratchets
    DOWN with the crash, so on a sustained slide the "target" exit prints far below
    entry. Seeing it fall alongside price is the whole reason for this chart.
  * the ROLLING 20m MIN (the entry channel)       -- every new low re-arms the signal
  * hi_120 / chan_lo_prev_120                     -- the 2m flush reference levels

Prices are shown RAW so the axis matches what was actually traded.
 IMPORTANT, and easy to get backwards: the STORED slim parquet is RAW --
Intraday.fs multiplies by adj_ratio at LOAD time ("vwap = raw x adj_ratio"), so the
adjustment lives in the engine, not in the file. Trip prices (entry_px / exit_px /
signal_vwap / the chan_* levels) ARE adjusted and must be divided by adj_ratio;
the bars must NOT be. Verified on BSFC 2022-01-21 (adj_ratio 1000: bars 4.26-5.14,
entry_px 4455.61) and CING 2023-12-28 (adj_ratio 12: bars 8.15-12.54, entry_px 106).

Usage:
    python scripts/visualization/flushfader_loser_charts.py \
        --trips "data/equity/flushfader/v34_flush2m/trips_p*.parquet" \
        --bars-dir data/intraday_1s_slim \
        --output-dir data/charts/flushfader_knives \
        --tkds ENSV:2022-03-08 BSFC:2022-01-21 BWV:2022-08-18 TMC:2022-03-08 SST:2022-04-08
"""

from __future__ import annotations

import argparse
import os

import duckdb
import plotly.graph_objects as go
from plotly.subplots import make_subplots

RTH_OPEN = 34200   # 09:30 ET, seconds since ET midnight
RTH_CLOSE = 57600  # 16:00 ET
CONTROLS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "chart_controls.js")


def hhmm(sec: int) -> str:
    return f"{sec // 3600:02d}:{(sec % 3600) // 60:02d}:{sec % 60:02d}"


def load_trips(con, trips_glob: str, tkds: list[tuple[str, str]]):
    pairs = " OR ".join(
        f"(symbol = '{s}' AND trade_date = '{d}')" for s, d in tkds
    )
    return con.execute(
        f"""
        SELECT symbol, trade_date, adj_ratio, signal_sec, signal_vwap, entry_sec, entry_px,
               exit_sec, exit_px, ret_exit, exit_reason, first_low_vwap,
               chan_lo_prev_120, hi_120, bars_since_first_low, lows_since_first_low
        FROM read_parquet('{trips_glob}')
        WHERE {pairs}
        ORDER BY symbol, trade_date, signal_sec
        """
    ).fetchall()


def load_bars(con, bars_dir: str, date: str, symbol: str):
    """1s tape for one ticker-day, plus the two rolling levels the engine uses."""
    return con.execute(
        f"""
        SELECT bucket, vwap, volume,
               max(vwap) OVER (ORDER BY bucket ROWS BETWEEN 300 PRECEDING AND 1 PRECEDING) AS max5m,
               min(vwap) OVER (ORDER BY bucket ROWS BETWEEN 1200 PRECEDING AND 1 PRECEDING) AS min20m
        FROM read_parquet('{bars_dir}/{date}.parquet')
        WHERE ticker = '{symbol}' AND bucket BETWEEN {RTH_OPEN} AND {RTH_CLOSE}
        ORDER BY bucket
        """
    ).fetchall()


def render(symbol: str, date: str, bars, trips, out_path: str) -> None:
    adj = trips[0][2] or 1.0
    x = [hhmm(b[0]) for b in bars]
    px = [b[1] for b in bars]          # bars are ALREADY raw -- do NOT divide
    vol = [b[2] for b in bars]
    max5 = [b[3] for b in bars]
    min20 = [b[4] for b in bars]

    fig = make_subplots(
        rows=2, cols=1, shared_xaxes=True, vertical_spacing=0.03,
        row_heights=[0.78, 0.22],
        subplot_titles=(f"{symbol} {date}  --  1s vwap (raw $, adj_ratio={adj:g})", "volume"),
    )

    fig.add_trace(go.Scattergl(x=x, y=px, name="1s vwap", mode="lines",
                               line=dict(color="#111827", width=1.1)), row=1, col=1)
    # THE POINT: the exit target follows price down.
    fig.add_trace(go.Scattergl(x=x, y=max5, name="prior 5m MAX (= exit target)", mode="lines",
                               line=dict(color="#059669", width=1.0, dash="dot")), row=1, col=1)
    fig.add_trace(go.Scattergl(x=x, y=min20, name="prior 20m MIN (= entry trigger)", mode="lines",
                               line=dict(color="#dc2626", width=1.0, dash="dot")), row=1, col=1)
    fig.add_trace(go.Bar(x=x, y=vol, name="volume", marker_color="#9ca3af"), row=2, col=1)

    sig_x, sig_y, ent_x, ent_y = [], [], [], []
    for t in trips:
        (_, _, ar, ssec, svwap, esec, epx, xsec, xpx, ret, reason, *_rest) = t
        ar = ar or 1.0
        sig_x.append(hhmm(ssec)); sig_y.append(svwap / ar)
        ent_x.append(hhmm(esec)); ent_y.append(epx / ar)
        good = ret > 0
        fig.add_trace(go.Scattergl(
            x=[hhmm(esec), hhmm(xsec)], y=[epx / ar, xpx / ar], mode="lines+markers",
            line=dict(color="#16a34a" if good else "#b91c1c", width=2.4),
            marker=dict(size=[7, 11], symbol=["circle", "x"]),
            name=f"{hhmm(esec)} -> {hhmm(xsec)}  {ret*100:+.1f}% ({reason})",
            hovertemplate=f"{ret*100:+.2f}% via {reason}<extra></extra>",
        ), row=1, col=1)

    fig.add_trace(go.Scattergl(x=sig_x, y=sig_y, mode="markers", name="signal (new 20m low)",
                               marker=dict(color="#f59e0b", size=9, symbol="triangle-down")),
                  row=1, col=1)

    # (the first_low_vwap / hi_120 / chan_lo_prev_120 hlines were removed -- they are
    # single constants read at signal time, so they plot as flat lines with no shape.
    # The ROLLING levels above are the informative ones.)

    net = sum(t[9] for t in trips) * 100
    fig.update_layout(
        title=f"{symbol} {date}  --  {len(trips)} trips, net {net:+.1f}%",
        height=880, hovermode="x unified", template="plotly_white",
        legend=dict(orientation="h", yanchor="bottom", y=1.02, x=0),
    )
    fig.update_xaxes(rangeslider_visible=False)

    post = None
    if os.path.exists(CONTROLS):
        with open(CONTROLS) as fh:
            post = fh.read()
    # scrollZoom is a plotly CONFIG flag -- chart_controls.js only supplies the
    # middle-click pan/zoom toggle and the a/s/d dragmode keys, so the config must
    # be passed too or the wheel does nothing.
    fig.write_html(out_path, config={"scrollZoom": True, "displayModeBar": True},
                   post_script=post, include_plotlyjs="cdn")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--trips", required=True)
    ap.add_argument("--bars-dir", default="data/intraday_1s_slim")
    ap.add_argument("--output-dir", required=True)
    ap.add_argument("--tkds", nargs="+", required=True, help="SYMBOL:YYYY-MM-DD ...")
    args = ap.parse_args()

    tkds = [tuple(t.split(":")) for t in args.tkds]
    os.makedirs(args.output_dir, exist_ok=True)
    con = duckdb.connect()

    rows = load_trips(con, args.trips, tkds)
    by_tkd: dict[tuple[str, str], list] = {}
    for r in rows:
        by_tkd.setdefault((r[0], r[1]), []).append(r)

    for (symbol, date), trips in by_tkd.items():
        bars = load_bars(con, args.bars_dir, date, symbol)
        if not bars:
            print(f"  !! no 1s bars for {symbol} {date}")
            continue
        out = os.path.join(args.output_dir, f"{symbol}_{date}.html")
        render(symbol, date, bars, trips, out)
        net = sum(t[9] for t in trips) * 100
        print(f"  {symbol} {date}: {len(trips)} trips, net {net:+6.1f}%  -> {out}")


if __name__ == "__main__":
    main()
