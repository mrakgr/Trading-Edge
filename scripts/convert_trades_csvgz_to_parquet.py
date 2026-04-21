#!/usr/bin/env python3
"""
Streaming conversion of Polygon trades .csv.gz -> zstd-compressed .parquet.

DuckDB's writer buffers too much on 3.5 GB compressed inputs (~12 GB decoded)
and blew past the 16 GB RAM on this box even with memory_limit=6GB. PyArrow's
streaming CSV reader plus ParquetWriter keeps a bounded window of batches in
memory and writes row groups as they fill, which is what we need.

Why pyarrow over polars: polars always emits large_string / large_list (64-bit
offsets) when it writes parquet, which inflates the on-disk size by ~14% vs.
DuckDB's 32-bit-offset encoding. That's ~150 GB of wasted space across the
full 1 TB trades dataset. pyarrow uses 32-bit offsets by default, matching
the DuckDB files.

Schema (from Polygon docs + a head-peek of a 2026 file):
    ticker                 STRING
    conditions             STRING -> LIST<UINT8>   (parsed from "12,37")
    correction             INT64
    exchange               INT64
    id                     INT64
    participant_timestamp  INT64
    price                  DOUBLE
    sequence_number        INT64
    sip_timestamp          INT64
    size                   DOUBLE
    tape                   INT64
    trf_id                 INT64
    trf_timestamp          INT64
"""

from __future__ import annotations

import argparse
import sys
import time
from concurrent.futures import ProcessPoolExecutor, as_completed
from pathlib import Path

import pyarrow as pa
import pyarrow.compute as pc
import pyarrow.csv as pac
import pyarrow.parquet as pq


def _transform_conditions(batch: pa.RecordBatch) -> pa.RecordBatch:
    """Turn the `conditions` VARCHAR column into LIST<UINT8>.

    NULL / ""       -> []
    "12"            -> [12]
    "12,37"         -> [12, 37]

    We keep the column in its original position (index 1) so the output
    schema matches the DuckDB-produced parquets byte-for-byte.
    """
    cond = batch.column("conditions")
    # Normalize the string: null -> "", which makes the later split produce
    # [""] for both cases, then filter out empty-string children before cast.
    cond_filled = pc.fill_null(cond, "")
    split = pc.split_pattern(cond_filled, pattern=",")
    # The input "" produces [""], which we need to turn into []. Use
    # list_filter to drop empty strings from each row's list.
    mask = pc.not_equal(pc.list_flatten(split), "")
    # Rebuild the list preserving row boundaries. list_element_wise filtering
    # isn't a single op in pyarrow; instead we flatten, filter, and regroup
    # using the original offsets recomputed from `mask`.
    nonempty_lists = pc.list_element(split, 0)  # unused — placeholder
    # Simpler approach: map empty-string rows to [] explicitly before split.
    empty_str_mask = pc.equal(cond_filled, "")
    empty_list = pa.array([[]] * len(batch), type=pa.list_(pa.string()))
    split = pc.if_else(empty_str_mask, empty_list, split)
    # Now cast list<string> -> list<uint8>. split has only digit-string or
    # empty-list children at this point, so cast is safe.
    cast_list = pc.cast(split, pa.list_(pa.uint8()))

    idx = batch.schema.get_field_index("conditions")
    return batch.set_column(idx, "conditions", cast_list)


def convert(csv_gz_path: Path, parquet_path: Path) -> None:
    # Read options: we let pyarrow auto-detect types but override `conditions`
    # to stay VARCHAR (we transform it per-batch below).
    convert_options = pac.ConvertOptions(
        column_types={"conditions": pa.string()}
    )
    # Small-ish batches keep memory bounded (~16k rows per batch by default;
    # we bump block_size to increase CSV read chunk).
    read_options = pac.ReadOptions(block_size=1 << 22)  # 4 MB chunks

    reader = pac.open_csv(
        csv_gz_path,
        read_options=read_options,
        convert_options=convert_options,
    )

    # Build the output schema by transforming one batch to establish field types.
    first = reader.read_next_batch()
    first = _transform_conditions(first)
    out_schema = first.schema

    # Default PyArrow encodings (dictionary + PLAIN + zstd) match what DuckDB
    # writes on this dataset. We're within a few % of DuckDB's sizes — good
    # enough.
    writer = pq.ParquetWriter(
        parquet_path,
        out_schema,
        compression="zstd",
        compression_level=3,
        use_dictionary=True,
    )
    try:
        # Group multiple CSV batches into a single parquet row group. Target
        # ~250k rows per row group, matching the DuckDB output density.
        target_rows = 256_000
        buf: list[pa.RecordBatch] = [first]
        buf_rows = first.num_rows

        def flush():
            nonlocal buf, buf_rows
            if buf_rows == 0:
                return
            tbl = pa.Table.from_batches(buf, schema=out_schema)
            writer.write_table(tbl, row_group_size=buf_rows)
            buf = []
            buf_rows = 0

        while True:
            try:
                batch = reader.read_next_batch()
            except StopIteration:
                break
            batch = _transform_conditions(batch)
            buf.append(batch)
            buf_rows += batch.num_rows
            if buf_rows >= target_rows:
                flush()
        flush()
    finally:
        writer.close()


def _run_one(csv_path: Path, parquet_path: Path, keep_csv: bool, idx: int, total: int) -> None:
    """Sequential worker: logs inline as it runs."""
    size_mb = csv_path.stat().st_size / (1024 * 1024)
    print(f"[{idx}/{total}] {csv_path.name} ({size_mb:,.0f} MB) -> {parquet_path.name}")
    start = time.time()
    try:
        convert(csv_path, parquet_path)
    except Exception as ex:
        print(f"  FAILED: {ex}", file=sys.stderr)
        if parquet_path.exists():
            try:
                parquet_path.unlink()
            except OSError:
                pass
        return
    elapsed = time.time() - start
    out_mb = parquet_path.stat().st_size / (1024 * 1024)
    print(f"  done in {elapsed:.1f}s, {out_mb:,.0f} MB parquet")
    if not keep_csv:
        csv_path.unlink()


def _run_one_result(csv_path: Path, parquet_path: Path, keep_csv: bool) -> str:
    """Parallel worker: returns a one-line summary for the main process to print."""
    size_mb = csv_path.stat().st_size / (1024 * 1024)
    start = time.time()
    convert(csv_path, parquet_path)
    elapsed = time.time() - start
    out_mb = parquet_path.stat().st_size / (1024 * 1024)
    if not keep_csv:
        csv_path.unlink()
    return f"{csv_path.name} ({size_mb:,.0f} MB) -> {out_mb:,.0f} MB in {elapsed:.1f}s"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[1])
    ap.add_argument("--input-dir", type=Path, default=Path("data/bulk/trades"))
    ap.add_argument(
        "--date",
        type=str,
        default=None,
        help="Convert only this YYYY-MM-DD (default: every .csv.gz in --input-dir)",
    )
    ap.add_argument(
        "--keep-csv",
        action="store_true",
        help="Do not delete the .csv.gz after a successful conversion.",
    )
    ap.add_argument(
        "--jobs",
        "-j",
        type=int,
        default=1,
        help="Parallel conversion workers. Peak RAM ~250 MB per worker.",
    )
    args = ap.parse_args()

    if args.date:
        csv_paths = [args.input_dir / f"{args.date}.csv.gz"]
    else:
        csv_paths = sorted(args.input_dir.glob("*.csv.gz"))

    if not csv_paths:
        print(f"No .csv.gz files found in {args.input_dir}", file=sys.stderr)
        return 1

    # Filter out already-done files and non-existent files up front so the
    # worker pool only sees real work.
    tasks = []
    for csv_path in csv_paths:
        if not csv_path.exists():
            print(f"{csv_path.name} — not found, skipping")
            continue
        parquet_path = csv_path.with_name(csv_path.stem.replace(".csv", "") + ".parquet")
        if parquet_path.exists() and parquet_path.stat().st_size > 0:
            print(f"{parquet_path.name} exists, skipping")
            continue
        tasks.append((csv_path, parquet_path))

    print(f"Converting {len(tasks)} files with {args.jobs} worker(s)...")
    total_start = time.time()
    nDone = 0

    if args.jobs <= 1:
        for csv_path, parquet_path in tasks:
            nDone += 1
            _run_one(csv_path, parquet_path, args.keep_csv, nDone, len(tasks))
    else:
        with ProcessPoolExecutor(max_workers=args.jobs) as pool:
            futures = {
                pool.submit(_run_one_result, csv_path, parquet_path, args.keep_csv): (csv_path, parquet_path)
                for csv_path, parquet_path in tasks
            }
            for fut in as_completed(futures):
                csv_path, parquet_path = futures[fut]
                nDone += 1
                try:
                    msg = fut.result()
                    print(f"[{nDone}/{len(tasks)}] {msg}")
                except Exception as ex:
                    print(f"[{nDone}/{len(tasks)}] {csv_path.name} FAILED: {ex}", file=sys.stderr)
                    if parquet_path.exists():
                        try:
                            parquet_path.unlink()
                        except OSError:
                            pass

    total_elapsed = time.time() - total_start
    print(f"\nTotal: {total_elapsed:.1f}s ({total_elapsed / 60:.1f} min)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
