"""Download Crypto Lake order-book data and write per-(symbol, date) parquets.

Wraps lakeapi.load_data, slicing the requested date range into single-day
windows and writing each to a parquet on /mnt/d. Atomic .tmp + os.replace so
power loss or interrupt can resume without corruption. Skip-if-exists.

Schema (passed through from Lake, with light reshaping):
  timestamp_us       int64    -- origin_time as microseconds since epoch
  received_time_us   int64    -- received_time as microseconds since epoch
  sequence_number    int64
  bid_0_price..bid_19_price   float64  -- bid_0 is best bid (top of book)
  bid_0_size..bid_19_size     float64
  ask_0_price..ask_19_price   float64  -- ask_0 is best ask
  ask_0_size..ask_19_size     float64

The exchange and symbol partition cols Lake adds are stripped; they're
already encoded in the output path.

Usage (free sample mode, BINANCE BTC-USDT):
    python scripts/crypto/download_book.py \\
        --symbol BTC-USDT --exchange BINANCE \\
        --start-date 2022-10-01 --end-date 2022-10-03

Usage (paid bucket — requires AWS creds in env or ~/.aws/credentials):
    python scripts/crypto/download_book.py --paid \\
        --symbol BTCUSDT-PERP --exchange BINANCE_FUTURES \\
        --start-date 2024-05-01 --end-date 2026-04-30
"""

from __future__ import annotations

import argparse
import datetime as dt
import os
import sys
import time

import lakeapi
import pandas as pd
import pyarrow as pa
import pyarrow.parquet as pq


def _to_us(series: pd.Series) -> pd.Series:
    """Convert a datetime64[ns] column to int64 microseconds since epoch."""
    return (series.astype("int64") // 1000).astype("int64")


def _output_path(output_dir: str, exchange: str, symbol: str, date: dt.date) -> str:
    return os.path.join(output_dir, exchange, symbol, f"{date.isoformat()}.parquet")


def _write_day_parquet(df: pd.DataFrame, out_path: str) -> None:
    """Reshape Lake's frame into our parquet and write atomically."""
    # Drop partition cols (encoded in path).
    df = df.drop(columns=[c for c in ("exchange", "symbol") if c in df.columns])

    # datetime → microseconds. The 'origin_time' field is the exchange-side
    # stamp; align downstream code on that.
    out = pd.DataFrame(index=df.index)
    out["timestamp_us"] = _to_us(df["origin_time"])
    out["received_time_us"] = _to_us(df["received_time"])
    out["sequence_number"] = df["sequence_number"].astype("int64")
    # All 80 level columns pass through unchanged.
    for col in df.columns:
        if col in ("origin_time", "received_time", "sequence_number"):
            continue
        out[col] = df[col]

    table = pa.Table.from_pandas(out, preserve_index=False)
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    tmp = out_path + ".tmp"
    if os.path.exists(tmp):
        os.remove(tmp)
    pq.write_table(table, tmp, compression="zstd", compression_level=3)
    os.replace(tmp, out_path)


def _date_range(start: dt.date, end: dt.date):
    d = start
    while d <= end:
        yield d
        d = d + dt.timedelta(days=1)


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--symbol", required=True,
                   help="Crypto Lake symbol, hyphenated (e.g. BTC-USDT, BTCUSDT-PERP).")
    p.add_argument("--exchange", default="BINANCE_FUTURES",
                   help="Exchange tag. Default BINANCE_FUTURES. Sample mode requires BINANCE for book.")
    p.add_argument("--start-date", required=True, help="Inclusive YYYY-MM-DD.")
    p.add_argument("--end-date", required=True, help="Inclusive YYYY-MM-DD.")
    p.add_argument("--table", default="book",
                   choices=["book", "book_delta", "book_1m", "trades", "level_1",
                            "candles", "funding", "open_interest", "liquidations"],
                   help="Lake table. Default: book.")
    p.add_argument("--output-dir", default="/mnt/d/trading-edge-bulk/crypto/lake",
                   help="Output root. Files land at {root}/{table}/{exchange}/{symbol}/{date}.parquet.")
    p.add_argument("--paid", action="store_true",
                   help="Use the paid bucket (requires AWS creds). Default: free sample bucket.")
    p.add_argument("--use-cache", action="store_true",
                   help="Let lakeapi use its .lake_cache directory. Default off — we cache as parquet ourselves.")
    args = p.parse_args()

    if not args.paid:
        lakeapi.use_sample_data(anonymous_access=True)
        print("Mode: free sample bucket (anonymous access)", file=sys.stderr)

    start = dt.date.fromisoformat(args.start_date)
    end = dt.date.fromisoformat(args.end_date)
    if start > end:
        print(f"start ({start}) > end ({end})", file=sys.stderr)
        return 2

    output_root = os.path.join(args.output_dir, args.table)

    n_done = n_skip = n_fail = 0
    sw = time.time()
    for d in _date_range(start, end):
        out_path = _output_path(output_root, args.exchange, args.symbol, d)
        if os.path.exists(out_path):
            n_skip += 1
            print(f"  skip {args.symbol} {d}: already on disk", file=sys.stderr)
            continue

        # lakeapi treats end as exclusive midnight, so request [d, d+1).
        try:
            df = lakeapi.load_data(
                table=args.table,
                start=dt.datetime.combine(d, dt.time.min),
                end=dt.datetime.combine(d + dt.timedelta(days=1), dt.time.min),
                symbols=[args.symbol],
                exchanges=[args.exchange],
                cached=args.use_cache,
            )
        except Exception as e:
            n_fail += 1
            print(f"  FAIL {args.symbol} {d}: {type(e).__name__}: {e}", file=sys.stderr)
            continue

        if df is None or len(df) == 0:
            n_fail += 1
            print(f"  empty {args.symbol} {d}", file=sys.stderr)
            continue

        try:
            _write_day_parquet(df, out_path)
            n_done += 1
            sz_mb = os.path.getsize(out_path) / 1e6
            print(f"  OK   {args.symbol} {d}: {len(df):,} rows, {sz_mb:.1f} MB",
                  file=sys.stderr)
        except Exception as e:
            n_fail += 1
            try:
                if os.path.exists(out_path + ".tmp"):
                    os.remove(out_path + ".tmp")
            except OSError:
                pass
            print(f"  FAIL write {args.symbol} {d}: {type(e).__name__}: {e}",
                  file=sys.stderr)

    elapsed = time.time() - sw
    print("", file=sys.stderr)
    print(f"Done in {elapsed:.0f}s. downloaded={n_done} skipped={n_skip} failed={n_fail}",
          file=sys.stderr)
    return 0 if n_fail == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
