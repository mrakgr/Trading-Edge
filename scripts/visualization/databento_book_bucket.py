"""Replay Databento MBO files for one ticker-day across all venues and emit
a compact event log of consolidated order-book changes.

The output is *change-only* long-format: a row appears only when the total
resting size at a (price, side) level — summed across venues — moves to a
new value.  Combined with forward-fill at render time this reconstructs the
exact book at any moment without storing one row per cell per bucket.

Timestamps are quantized to the chosen bucket (default 100ms).  Within a
single bucket multiple updates to the same (price, side) collapse into the
final value, so the row count tracks the number of distinct cell-states
across buckets, not the raw event count.

Outputs
-------
  data/databento/book/<TICKER>/<DATE>/levels.parquet   — book change log
  data/databento/book/<TICKER>/<DATE>/trades.parquet   — execution overlay

levels.parquet schema:
    bucket_ts_ns   int64   bucket start, ns since epoch (UTC)
    price_cents    int32   price in cents
    side           uint8   1 = bid, 2 = ask
    size           int32   total resting shares across all venues *as of this bucket*

trades.parquet schema:
    ts_ns          int64   execution timestamp (full nanosecond precision)
    price_cents    int32
    size           int32
    aggressor      uint8   0 = unknown / N-side, 1 = buy, 2 = sell

Replay semantics
----------------
- R (reset): clears that venue's contribution.
- A (add): order added at (price, side) for `size`.
- C (cancel): size>0 = partial cancel; size=0 or >= prior = full cancel.
- M (modify): remove at old (price, side), add at new.
- F (fill): displayed-order execution, decrements resting; emits trade.
- T side=A/B (hidden): emits trade with aggressor = inverse of side.
- T side=N (cross / dark-vs-dark): emits trade with aggressor = 0.
- Price sentinel INT64_MAX skipped.
"""

import argparse
import heapq
import sys
from datetime import datetime
from pathlib import Path

import databento as db
import polars as pl


PRICE_SCALE = 1_000_000_000
INT64_MAX = 2**63 - 1
DEFAULT_BUCKET_MS = 100


def replay_venue(path: Path):
    """Generator of per-venue book/trade events.  See bucket_replay docstring."""
    rs = db.DBNStore.from_file(path)
    book: dict[int, tuple[int, int, int]] = {}   # order_id -> (side_byte, price_cents, size)
    for rec in rs:
        ts = int(rec.ts_event)
        action = str(rec.action)
        side = str(rec.side)
        size = int(rec.size)
        price_raw = int(rec.price)
        order_id = int(rec.order_id)

        if action == 'R':
            yield ('reset', ts)
            book.clear()
            continue

        if price_raw == INT64_MAX:
            continue

        price_cents = price_raw // (PRICE_SCALE // 100)
        side_b = 1 if side == 'B' else (2 if side == 'A' else 0)

        if action == 'A':
            if side_b == 0:
                continue
            book[order_id] = (side_b, price_cents, size)
            yield ('book', ts, side_b, price_cents, +size)

        elif action == 'C':
            prev = book.pop(order_id, None)
            if prev is None:
                continue
            pside, pprice, psize = prev
            if size == 0 or size >= psize:
                yield ('book', ts, pside, pprice, -psize)
            else:
                book[order_id] = (pside, pprice, psize - size)
                yield ('book', ts, pside, pprice, -size)

        elif action == 'M':
            prev = book.pop(order_id, None)
            if prev is not None:
                pside, pprice, psize = prev
                yield ('book', ts, pside, pprice, -psize)
            if side_b != 0:
                book[order_id] = (side_b, price_cents, size)
                yield ('book', ts, side_b, price_cents, +size)

        elif action == 'F':
            prev = book.get(order_id)
            if prev is not None:
                pside, pprice, psize = prev
                new_size = psize - size
                if new_size <= 0:
                    del book[order_id]
                    yield ('book', ts, pside, pprice, -psize)
                else:
                    book[order_id] = (pside, pprice, new_size)
                    yield ('book', ts, pside, pprice, -size)
                aggressor = 2 if pside == 1 else 1
                yield ('trade', ts, aggressor, pprice, size)
            else:
                aggressor = 2 if side_b == 1 else (1 if side_b == 2 else 0)
                yield ('trade', ts, aggressor, price_cents, size)

        elif action == 'T':
            if side_b == 1:
                aggressor = 2
            elif side_b == 2:
                aggressor = 1
            else:
                aggressor = 0
            yield ('trade', ts, aggressor, price_cents, size)


def merge_venues(venue_paths):
    iters = []
    for idx, p in enumerate(venue_paths):
        def tagged(gen, vi=idx):
            for ev in gen:
                yield (ev[1], vi, ev)
        iters.append(tagged(replay_venue(p)))
    yield from heapq.merge(*iters, key=lambda x: x[0])


def bucket_replay(venue_paths, bucket_ns, window_start_ns, window_end_ns):
    """Run consolidated replay, emit one row per consolidated (price, side, size)
    change within each bucket.

    Strategy:
    - Maintain per-venue running totals: dict[(venue_idx, side, price)] -> size
    - Maintain consolidated last-emitted values: dict[(side, price)] -> size
    - Within each bucket, accumulate which (side, price) cells were touched
    - At bucket boundary, for each touched cell, compute current consolidated
      size from per-venue totals; if it differs from last-emitted, emit a row
      and update last-emitted.
    """
    # Per-venue running: dict[(venue_idx, side, price)] -> size
    running: dict[tuple[int, int, int], int] = {}
    # Consolidated totals across venues, kept incrementally
    consolidated: dict[tuple[int, int], int] = {}
    # Last-emitted consolidated value per (side, price)
    last_emitted: dict[tuple[int, int], int] = {}
    # Cells touched in the current bucket
    touched: set[tuple[int, int]] = set()

    level_buckets: list[int] = []
    level_prices: list[int] = []
    level_sides: list[int] = []
    level_sizes: list[int] = []

    trade_ts: list[int] = []
    trade_prices: list[int] = []
    trade_sizes: list[int] = []
    trade_aggressors: list[int] = []

    n_events = 0
    pre_window_events = 0
    current_bucket_ns = -1

    def flush(bucket_ts_ns):
        # For each cell touched this bucket, emit only if consolidated total differs from last-emitted.
        for cell in touched:
            cur = consolidated.get(cell, 0)
            prev = last_emitted.get(cell, 0)
            if cur != prev:
                side, price = cell
                level_buckets.append(bucket_ts_ns)
                level_prices.append(price)
                level_sides.append(side)
                level_sizes.append(cur)
                if cur == 0:
                    last_emitted.pop(cell, None)
                else:
                    last_emitted[cell] = cur

    for ts_ns, venue_idx, ev in merge_venues(venue_paths):
        n_events += 1
        if ts_ns < window_start_ns:
            pre_window_events += 1
        if ts_ns >= window_end_ns:
            break

        # Quantize event time to bucket start
        bucket_ts = ((ts_ns - window_start_ns) // bucket_ns) * bucket_ns + window_start_ns
        if bucket_ts != current_bucket_ns:
            # Flush previous bucket
            if current_bucket_ns >= window_start_ns and touched:
                flush(current_bucket_ns)
            touched = set()
            current_bucket_ns = bucket_ts

        kind = ev[0]
        if kind == 'reset':
            # Drop this venue's contribution; decrement consolidated for each cell it held
            to_drop = [(vi, s, p) for (vi, s, p) in running.keys() if vi == venue_idx]
            for k in to_drop:
                sz = running.pop(k)
                cell = (k[1], k[2])
                new_total = consolidated.get(cell, 0) - sz
                if new_total > 0:
                    consolidated[cell] = new_total
                else:
                    consolidated.pop(cell, None)
                touched.add(cell)
            continue

        if kind == 'book':
            _, _, side_b, price_cents, size_delta = ev
            if side_b == 0:
                continue
            key = (venue_idx, side_b, price_cents)
            running[key] = running.get(key, 0) + size_delta
            if running[key] <= 0:
                running.pop(key, None)
            cell = (side_b, price_cents)
            new_total = consolidated.get(cell, 0) + size_delta
            if new_total > 0:
                consolidated[cell] = new_total
            else:
                consolidated.pop(cell, None)
            touched.add(cell)

        elif kind == 'trade':
            _, _, aggressor, price_cents, size_qty = ev
            if ts_ns >= window_start_ns:
                trade_ts.append(ts_ns)
                trade_prices.append(price_cents)
                trade_sizes.append(size_qty)
                trade_aggressors.append(aggressor)

    # Final flush
    if current_bucket_ns >= window_start_ns and touched:
        flush(current_bucket_ns)

    print(f"  events processed: {n_events:,} (pre-window: {pre_window_events:,})")
    print(f"  level rows      : {len(level_buckets):,}")
    print(f"  trade rows      : {len(trade_ts):,}")

    return {
        'level_bucket_ns': level_buckets,
        'level_price_cents': level_prices,
        'level_side': level_sides,
        'level_size': level_sizes,
        'trade_ts_ns': trade_ts,
        'trade_price_cents': trade_prices,
        'trade_size': trade_sizes,
        'trade_aggressor': trade_aggressors,
    }


def parse_args():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--ticker", required=True)
    ap.add_argument("--date", required=True, help="YYYY-MM-DD")
    ap.add_argument("--bucket-ms", type=int, default=DEFAULT_BUCKET_MS,
                    help=f"Time-quantization granularity in ms (default {DEFAULT_BUCKET_MS}).")
    ap.add_argument("--mbo-dir", default=None)
    ap.add_argument("--out-dir", default=None)
    return ap.parse_args()


def main():
    args = parse_args()
    mbo_dir = Path(args.mbo_dir) if args.mbo_dir else Path("data/databento/mbo") / args.ticker / args.date
    if not mbo_dir.exists():
        print(f"Missing: {mbo_dir}", file=sys.stderr)
        sys.exit(2)
    out_dir = Path(args.out_dir) if args.out_dir else Path("data/databento/book") / args.ticker / args.date
    out_dir.mkdir(parents=True, exist_ok=True)

    venue_paths = sorted(mbo_dir.glob("*.dbn.zst"))
    print(f"Replaying {len(venue_paths)} venue files from {mbo_dir}")
    for p in venue_paths:
        print(f"  {p.stem:<6} {p.stat().st_size/(1024*1024):>7.2f} MB")

    import zoneinfo
    et = zoneinfo.ZoneInfo("America/New_York")
    day = datetime.strptime(args.date, "%Y-%m-%d").date()
    window_start = datetime.combine(day, datetime.min.time().replace(hour=4), tzinfo=et)
    window_end = datetime.combine(day, datetime.min.time().replace(hour=20), tzinfo=et)
    window_start_ns = int(window_start.timestamp() * 1e9)
    window_end_ns = int(window_end.timestamp() * 1e9)
    bucket_ns = args.bucket_ms * 1_000_000
    print(f"Window: {window_start} → {window_end}")
    print(f"Bucket: {args.bucket_ms}ms")

    data = bucket_replay(venue_paths, bucket_ns, window_start_ns, window_end_ns)

    levels_df = pl.DataFrame({
        'bucket_ts_ns': pl.Series(data['level_bucket_ns'], dtype=pl.Int64),
        'price_cents':  pl.Series(data['level_price_cents'], dtype=pl.Int32),
        'side':         pl.Series(data['level_side'], dtype=pl.UInt8),
        'size':         pl.Series(data['level_size'], dtype=pl.Int32),
    })
    trades_df = pl.DataFrame({
        'ts_ns':        pl.Series(data['trade_ts_ns'], dtype=pl.Int64),
        'price_cents':  pl.Series(data['trade_price_cents'], dtype=pl.Int32),
        'size':         pl.Series(data['trade_size'], dtype=pl.Int32),
        'aggressor':    pl.Series(data['trade_aggressor'], dtype=pl.UInt8),
    })

    levels_path = out_dir / "levels.parquet"
    trades_path = out_dir / "trades.parquet"
    levels_df.write_parquet(levels_path, compression='zstd')
    trades_df.write_parquet(trades_path, compression='zstd')

    lsize = levels_path.stat().st_size / (1024*1024)
    tsize = trades_path.stat().st_size / (1024*1024)
    print(f"Wrote {levels_path} ({lsize:.2f} MB, {len(levels_df):,} rows)")
    print(f"Wrote {trades_path} ({tsize:.2f} MB, {len(trades_df):,} rows)")


if __name__ == '__main__':
    main()
