"""Interactive order-book heatmap from the change-log parquet produced by
databento_book_bucket.py.

Runs as a Panel/Bokeh server app. Datashader re-rasterizes on every pan/zoom
so you can drill from full-day overview down to single-second detail at the
underlying 100ms resolution without re-running the bucketer.

Usage
-----
    panel serve scripts/visualization/databento_book_view.py --show \\
                --args --ticker EOSE --date 2026-05-13

By default it binds to localhost:5006. Pan/zoom with the Bokeh toolbar at
the right of the plot. The scroll wheel zooms the time axis; hold shift +
scroll to zoom price.

Strategy
--------
The change-log is sparse: a row appears only when consolidated size at a
(price, side) cell changes. To turn that into a continuous heatmap we
materialise each row as a *rectangle* spanning from its bucket to the next
bucket where the same (price, side) cell next changes. Datashader's
Rectangles aggregator rasterizes the rectangles at the current viewport.

Bids and asks share one signed-size column (bid +, ask -) and are rendered
with a diverging blue/red colormap. Trades overlay as colored points.
"""

import argparse
import sys
from pathlib import Path

import holoviews as hv
import numpy as np
import pandas as pd
import panel as pn
import polars as pl
from holoviews.operation.datashader import rasterize


hv.extension('bokeh')
pn.extension()


def parse_args():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--ticker", required=True)
    ap.add_argument("--date", required=True, help="YYYY-MM-DD")
    ap.add_argument("--in-dir", default=None,
                    help="Override input dir. Default: data/databento/book/<TICKER>/<DATE>")
    ap.add_argument("--price-pad", type=float, default=0.20,
                    help="Padding in dollars above/below the day's traded range (default 0.20)")
    return ap.parse_args()


def build_app(ticker, date_str, in_dir, price_pad):
    levels = pl.read_parquet(in_dir / "levels.parquet")
    trades = pl.read_parquet(in_dir / "trades.parquet")
    print(f"Loaded {len(levels):,} level rows, {len(trades):,} trade rows")

    bucket_ts = levels.select('bucket_ts_ns').unique().sort('bucket_ts_ns')['bucket_ts_ns']
    if len(bucket_ts) > 1:
        diffs = bucket_ts.diff().drop_nulls()
        bucket_ns = int(diffs.min())
    else:
        bucket_ns = 100_000_000
    print(f"Detected bucket: {bucket_ns/1e6:.0f}ms")

    levels = levels.sort(['side', 'price_cents', 'bucket_ts_ns'])
    eod_ns = int(bucket_ts.max()) + bucket_ns
    levels = levels.with_columns(
        next_ts_ns=pl.col('bucket_ts_ns')
            .shift(-1)
            .over(['side', 'price_cents'])
            .fill_null(eod_ns)
    )
    rect = levels.filter(pl.col('size') > 0)
    print(f"  {len(rect):,} rectangles")

    if len(trades) > 0:
        traded_lo = int(trades['price_cents'].min())
        traded_hi = int(trades['price_cents'].max())
    else:
        traded_lo = int(rect['price_cents'].min())
        traded_hi = int(rect['price_cents'].max())
    pad_cents = int(price_pad * 100)
    p_lo = (traded_lo - pad_cents) / 100.0
    p_hi = (traded_hi + pad_cents) / 100.0
    print(f"Price range: ${p_lo:.2f} → ${p_hi:.2f}")

    rect_df = rect.to_pandas()
    # Convert to ET-local naive datetimes so the Bokeh axis ticks read in ET
    # (Bokeh's datetime axis treats whatever naive datetime it receives as
    # the displayed value, without any timezone reinterpretation).
    rect_df['x0'] = (pd.to_datetime(rect_df['bucket_ts_ns'], unit='ns', utc=True)
                     .dt.tz_convert('America/New_York').dt.tz_localize(None))
    rect_df['x1'] = (pd.to_datetime(rect_df['next_ts_ns'], unit='ns', utc=True)
                     .dt.tz_convert('America/New_York').dt.tz_localize(None))
    rect_df['y0'] = (rect_df['price_cents'] - 0.5) / 100.0
    rect_df['y1'] = (rect_df['price_cents'] + 0.5) / 100.0
    rect_df['signed_size'] = np.where(rect_df['side'] == 1, rect_df['size'], -rect_df['size'])

    rectangles = hv.Rectangles(
        rect_df[['x0', 'y0', 'x1', 'y1', 'signed_size']],
        kdims=['x0', 'y0', 'x1', 'y1'],
        vdims=['signed_size'],
    )

    x_lo = rect_df['x0'].min()
    x_hi = rect_df['x1'].max()

    # dynamic=True: Datashader re-rasterizes on every pan/zoom.
    book_layer = rasterize(rectangles, aggregator='sum', dynamic=True).opts(
        cmap='RdBu_r',
        cnorm='eq_hist',
        symmetric=True,
        clipping_colors={'NaN': 'white'},
        colorbar=True,
        responsive=True,
        height=900,
        xlim=(x_lo, x_hi),
        ylim=(p_lo, p_hi),
        title=f"{ticker} {date_str} — Consolidated Order Book",
        xlabel='Time (ET)',
        ylabel='Price',
        tools=['xwheel_zoom', 'ywheel_zoom', 'pan', 'box_zoom', 'reset'],
        active_tools=['xwheel_zoom'],
    )

    if len(trades) > 0:
        td = trades.to_pandas()
        td['ts'] = (pd.to_datetime(td['ts_ns'], unit='ns', utc=True)
                    .dt.tz_convert('America/New_York').dt.tz_localize(None))
        td['price'] = td['price_cents'] / 100.0
        td['color'] = td['aggressor'].map({1: 'green', 2: 'red', 0: 'grey'}).fillna('grey')
        td['msize'] = np.clip(np.log10(np.maximum(td['size'], 1)) * 3, 2, 12)
        trades_points = hv.Points(
            td[['ts', 'price', 'color', 'msize']],
            kdims=['ts', 'price'],
            vdims=['color', 'msize'],
        ).opts(
            color='color', size='msize', alpha=0.5,
            tools=['hover'],
        )
        view = book_layer * trades_points
    else:
        view = book_layer

    return view


def main():
    args = parse_args()
    in_dir = Path(args.in_dir) if args.in_dir else Path("data/databento/book") / args.ticker / args.date
    if not in_dir.exists():
        print(f"Missing: {in_dir}", file=sys.stderr)
        sys.exit(2)
    view = build_app(args.ticker, args.date, in_dir, args.price_pad)
    pane = pn.pane.HoloViews(view, sizing_mode='stretch_both')
    pane.servable()


# `panel serve` re-imports this module on each request, so the args parser
# and build_app run at module import time when invoked via panel serve.
main()
