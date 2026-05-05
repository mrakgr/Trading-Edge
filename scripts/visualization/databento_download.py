"""Download trade-level futures data from DataBento and write it as a CSV
shaped for futures_volume.py.

Output columns: trade_id, price, quantity, quote_quantity, timestamp_us,
is_buyer_maker. Aggressor mapping: DataBento side 'A' (ask-aggressor) is a
market sell -> is_buyer_maker=True; 'B' is a market buy -> is_buyer_maker=False;
'N' (auction/implied) rows are dropped.

Default dataset is GLBX.MDP3 (CME Globex). Default schema is 'trades'.
Continuous front-month symbols use the .c.0 suffix (e.g. ES.c.0, ZN.c.0)
and require stype_in='continuous'.
"""

import argparse
import json
import os
import sys
from datetime import date, datetime, timedelta, timezone
from pathlib import Path

import databento as db
import pandas as pd


REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_COST_LIMIT_USD = 5.0


def load_api_key():
    with open(REPO_ROOT / "api_key.json") as f:
        return json.load(f)["databento_api_key"]


def parse_args():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--symbol", required=True,
                    help="Continuous front-month symbol, e.g. ES.c.0 or ZN.c.0")
    ap.add_argument("--date", required=True,
                    help="UTC day to download (YYYY-MM-DD). Pulls [date 00:00, date+1 00:00) UTC.")
    ap.add_argument("--out", required=True, help="Output CSV path")
    ap.add_argument("--dataset", default="GLBX.MDP3",
                    help="DataBento dataset (default GLBX.MDP3 = CME Globex)")
    ap.add_argument("--schema", default="trades", help="DataBento schema (default 'trades')")
    ap.add_argument("--stype-in", default="continuous",
                    help="Symbology type for input symbols (default 'continuous' for .c.0 syntax)")
    ap.add_argument("--cost-limit", type=float, default=DEFAULT_COST_LIMIT_USD,
                    help=f"Refuse to download if estimated cost exceeds this (USD). Default ${DEFAULT_COST_LIMIT_USD:g}.")
    ap.add_argument("--force", action="store_true",
                    help="Skip the cost-limit check and download regardless.")
    return ap.parse_args()


def main():
    args = parse_args()
    day = datetime.strptime(args.date, "%Y-%m-%d").date()
    start = datetime.combine(day, datetime.min.time(), tzinfo=timezone.utc)
    end = start + timedelta(days=1)

    client = db.Historical(load_api_key())

    cost = client.metadata.get_cost(
        dataset=args.dataset,
        start=start,
        end=end,
        symbols=[args.symbol],
        schema=args.schema,
        stype_in=args.stype_in,
    )
    print(f"Estimated cost: ${cost:.4f} USD for {args.symbol} {args.date} {args.schema}")
    if not args.force and cost > args.cost_limit:
        print(f"Refusing: cost ${cost:.4f} exceeds limit ${args.cost_limit:.2f}. "
              f"Re-run with --force or raise --cost-limit.", file=sys.stderr)
        sys.exit(2)

    print(f"Requesting trades from {start.isoformat()} to {end.isoformat()}...")
    store = client.timeseries.get_range(
        dataset=args.dataset,
        start=start,
        end=end,
        symbols=[args.symbol],
        schema=args.schema,
        stype_in=args.stype_in,
    )

    df = store.to_df()
    print(f"Got {len(df):,} records")
    if len(df) == 0:
        print("No trades returned. Aborting.", file=sys.stderr)
        sys.exit(1)

    # Drop auction/implied prints (no aggressor side).
    n_before = len(df)
    df = df[df["side"].isin(["A", "B"])].copy()
    n_dropped = n_before - len(df)
    if n_dropped:
        print(f"Dropped {n_dropped:,} side='N' rows (auction/implied with no aggressor)")

    # to_df() returns timestamps as a DatetimeIndex (ns-resolution UTC).
    ts_ns = df.index.astype("int64").to_numpy()
    if not (ts_ns[1:] >= ts_ns[:-1]).all():
        print("Warning: trades not monotonically time-sorted; sorting.", file=sys.stderr)
        order = ts_ns.argsort(kind="stable")
        df = df.iloc[order].copy()
        ts_ns = ts_ns[order]
    ts_us = ts_ns // 1000

    # Aggressor -> is_buyer_maker: 'A' (ask-aggressor sell) -> True, 'B' (bid-aggressor buy) -> False.
    is_buyer_maker = (df["side"].to_numpy() == "A")

    # to_df() exposes prices as floats already (the integer 1e-9-scaled column is
    # the raw 'price'; pandas to_df converts it). Use whatever the float column is.
    price = df["price"].to_numpy(dtype="float64")
    qty = df["size"].to_numpy(dtype="float64")
    quote = price * qty

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)

    out_df = pd.DataFrame({
        "trade_id": range(1, len(df) + 1),
        "price": price,
        "quantity": qty,
        "quote_quantity": quote,
        "timestamp": ts_us,
        "is_buyer_maker": is_buyer_maker,
    })
    out_df.to_csv(out_path, header=False, index=False)
    print(f"Wrote {len(out_df):,} rows -> {out_path}")
    span_us = ts_us[-1] - ts_us[0]
    print(f"Time span: {fmt_us(int(ts_us[0]))} -> {fmt_us(int(ts_us[-1]))} ({span_us/1e6/3600:.2f} hrs)")
    n_buy = int((~is_buyer_maker).sum())
    n_sell = int(is_buyer_maker.sum())
    print(f"Aggressor breakdown: {n_buy:,} buy / {n_sell:,} sell ({n_buy/(n_buy+n_sell)*100:.1f}% buy)")


def fmt_us(us):
    return datetime.fromtimestamp(us / 1e6, tz=timezone.utc).strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]


if __name__ == "__main__":
    main()
