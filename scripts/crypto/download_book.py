"""Download Crypto Lake order-book data and write per-(symbol, date) parquets.

Wraps lakeapi.load_data, slicing the requested date range into single-day
windows and writing each to a parquet on /mnt/d. Atomic .tmp + os.replace so
power loss or interrupt can resume without corruption. Skip-if-exists.

book schema (passed through from Lake, with light reshaping):
  timestamp_us       int64    -- origin_time as microseconds since epoch
  received_time_us   int64    -- received_time as microseconds since epoch
  sequence_number    int64
  bid_0_price..bid_19_price   float64  -- bid_0 is best bid (top of book)
  bid_0_size..bid_19_size     float64
  ask_0_price..ask_19_price   float64  -- ask_0 is best ask
  ask_0_size..ask_19_size     float64

book_delta_v2 schema (raw-S3 path; lakeapi doesn't support this table):
  timestamp_us       int64    -- exchange-side, ns -> us
  received_time_us   int64    -- gateway-side, ns -> us
  sequence_number    int64
  side_is_bid        bool
  price              float64
  size               float64  -- post-update size; 0 means level removed

The exchange and symbol partition cols Lake adds are stripped; they're
already encoded in the output path.

Usage (free sample mode, BINANCE BTC-USDT):
    python scripts/crypto/download_book.py \\
        --symbol BTC-USDT --exchange BINANCE \\
        --start-date 2022-10-01 --end-date 2022-10-03

Usage (paid bucket — uses the 'crypto-lake' AWS profile):
    python scripts/crypto/download_book.py --paid \\
        --symbol BTC-USDT-PERP --exchange BINANCE_FUTURES \\
        --start-date 2024-05-01 --end-date 2026-04-30

Usage (paid bucket, book_delta_v2 — raw S3, bypasses lakeapi):
    python scripts/crypto/download_book.py --paid --table book_delta_v2 \\
        --symbol BTC-USDT-PERP --exchange BINANCE_FUTURES \\
        --start-date 2026-01-31 --end-date 2026-01-31
"""

from __future__ import annotations

import argparse
import datetime as dt
import os
import sys
import time

import boto3
import lakeapi
import pandas as pd
import pyarrow as pa
import pyarrow.fs as pafs
import pyarrow.parquet as pq

# book_delta_v2 lives in Crypto Lake's S3 bucket but is NOT exposed via
# lakeapi.load_data (their Python wrapper only knows the legacy 'book_delta'
# prefix, which is empty). We fetch directly from the bucket.
DELTA_V2_BUCKET = "qnt.data"
DELTA_V2_PREFIX = "market-data/cryptofeed/book_delta_v2"


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


def _delta_v2_s3_key(exchange: str, symbol: str, date: dt.date) -> str:
    return (
        f"{DELTA_V2_PREFIX}/exchange={exchange}/symbol={symbol}"
        f"/dt={date.isoformat()}/1.snappy.parquet"
    )


def _download_delta_v2(
    fs: pafs.S3FileSystem, exchange: str, symbol: str, date: dt.date, out_path: str
) -> int:
    """Stream the day's book_delta_v2 parquet from S3, normalise the schema,
    and write atomically. Streams row-group at a time to keep peak RSS bounded
    (a full day decompresses to ~5 GB, which OOMs a 16 GB box).
    Returns row count."""
    key = f"{DELTA_V2_BUCKET}/{_delta_v2_s3_key(exchange, symbol, date)}"
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    tmp = out_path + ".tmp"
    if os.path.exists(tmp):
        os.remove(tmp)

    out_schema = pa.schema([
        pa.field("timestamp_us", pa.int64()),
        pa.field("received_time_us", pa.int64()),
        pa.field("sequence_number", pa.int64()),
        pa.field("side_is_bid", pa.bool_()),
        pa.field("price", pa.float64()),
        pa.field("size", pa.float64()),
    ])

    n_rows = 0
    # The on-disk parquet has thousands of tiny row groups (~10 k rows each).
    # Read in batches of N row groups so each batch is sizable enough to
    # amortise per-batch overhead but small enough to fit in RAM.
    with fs.open_input_file(key) as src:
        pf = pq.ParquetFile(src)
        with pq.ParquetWriter(tmp, out_schema, compression="zstd", compression_level=3) as writer:
            BATCH = 64
            for start in range(0, pf.num_row_groups, BATCH):
                stop = min(start + BATCH, pf.num_row_groups)
                tbl = pf.read_row_groups(
                    list(range(start, stop)),
                    columns=["timestamp", "receipt_timestamp", "sequence_number",
                             "side_is_bid", "price", "size"],
                )
                # ns -> us via int64 floor div on the already-int64 columns.
                ts_us = pa.compute.divide(tbl["timestamp"], 1000)
                rcv_us = pa.compute.divide(tbl["receipt_timestamp"], 1000)
                out_tbl = pa.Table.from_arrays(
                    [
                        ts_us.cast(pa.int64()),
                        rcv_us.cast(pa.int64()),
                        tbl["sequence_number"].cast(pa.int64()),
                        tbl["side_is_bid"].cast(pa.bool_()),
                        tbl["price"].cast(pa.float64()),
                        tbl["size"].cast(pa.float64()),
                    ],
                    schema=out_schema,
                )
                writer.write_table(out_tbl)
                n_rows += out_tbl.num_rows

    os.replace(tmp, out_path)
    return n_rows


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
                   choices=["book", "book_delta", "book_delta_v2", "book_1m", "trades", "level_1",
                            "candles", "funding", "open_interest", "liquidations"],
                   help="Lake table. Default: book. 'book_delta_v2' uses raw S3 (bypasses lakeapi).")
    p.add_argument("--output-dir", default="/mnt/d/trading-edge-bulk/crypto/lake",
                   help="Output root. Files land at {root}/{table}/{exchange}/{symbol}/{date}.parquet.")
    p.add_argument("--paid", action="store_true",
                   help="Use the paid bucket (via 'crypto-lake' AWS profile). Default: free sample bucket.")
    p.add_argument("--aws-profile", default="crypto-lake",
                   help="Named AWS profile to use in --paid mode. Default: 'crypto-lake'.")
    p.add_argument("--use-cache", action="store_true",
                   help="Let lakeapi use its .lake_cache directory. Default off — we cache as parquet ourselves.")
    args = p.parse_args()

    boto_session = None
    delta_fs = None
    if not args.paid:
        if args.table == "book_delta_v2":
            print("--table book_delta_v2 requires --paid (no sample-bucket equivalent)",
                  file=sys.stderr)
            return 2
        lakeapi.use_sample_data(anonymous_access=True)
        print("Mode: free sample bucket (anonymous access)", file=sys.stderr)
    else:
        boto_session = boto3.Session(profile_name=args.aws_profile)
        print(f"Mode: paid bucket (boto3 profile={args.aws_profile!r})", file=sys.stderr)
        if args.table == "book_delta_v2":
            creds = boto_session.get_credentials().get_frozen_credentials()
            delta_fs = pafs.S3FileSystem(
                access_key=creds.access_key,
                secret_key=creds.secret_key,
                session_token=creds.token,
                region="eu-west-1",
            )

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

        if args.table == "book_delta_v2":
            try:
                n_rows = _download_delta_v2(
                    delta_fs, args.exchange, args.symbol, d, out_path
                )
            except FileNotFoundError as e:
                n_fail += 1
                print(f"  miss {args.symbol} {d}: {e}", file=sys.stderr)
                continue
            except Exception as e:
                n_fail += 1
                try:
                    if os.path.exists(out_path + ".tmp"):
                        os.remove(out_path + ".tmp")
                except OSError:
                    pass
                print(f"  FAIL {args.symbol} {d}: {type(e).__name__}: {e}",
                      file=sys.stderr)
                continue
            sz_mb = os.path.getsize(out_path) / 1e6
            n_done += 1
            print(f"  OK   {args.symbol} {d}: {n_rows:,} rows, {sz_mb:.1f} MB",
                  file=sys.stderr)
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
                boto3_session=boto_session,
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
