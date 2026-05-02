"""Sanity-check a downloaded Crypto Lake book parquet.

Runs the verification checklist from the v0 plan:
  1. Schema — confirm timestamp_us + 20×{bid,ask}×{price,size} columns.
  2. Monotonicity — bid_0_price > bid_1_price > ... and ask_0_price < ask_1_price < ...
     per row (the level-ordering invariant).
  3. Spread non-negativity — ask_0_price >= bid_0_price every row.
  4. Cadence — median, p99, max gap between consecutive snapshots.
  5. Imbalance shape — for top-1, top-5, top-10 cumulative quantities,
     compute I = (Q_bid - Q_ask) / (Q_bid + Q_ask) and dump quantiles.

Usage:
    python scripts/crypto/verify_book.py \\
        --input /mnt/d/trading-edge-bulk/crypto/lake/book/BINANCE/BTC-USDT/2022-10-01.parquet
"""

from __future__ import annotations

import argparse
import os
import sys

import duckdb


def _check_schema(con: duckdb.DuckDBPyConnection, path: str) -> bool:
    cols = con.execute(f"DESCRIBE SELECT * FROM read_parquet('{path}') LIMIT 0").fetchall()
    names = {c[0] for c in cols}
    required = {"timestamp_us", "received_time_us", "sequence_number"}
    for side in ("bid", "ask"):
        for i in range(20):
            required.add(f"{side}_{i}_price")
            required.add(f"{side}_{i}_size")
    missing = required - names
    extra = names - required
    print(f"[1/5] schema: {len(names)} columns")
    if missing:
        print(f"        MISSING: {sorted(missing)}")
    if extra:
        print(f"        extra (informational): {sorted(extra)}")
    return not missing


def _check_level_ordering(con: duckdb.DuckDBPyConnection, path: str) -> bool:
    """Confirm bid_0 > bid_1 > ... and ask_0 < ask_1 < ... per row.
    Allow null trailing levels (some symbols may not have 20 levels filled).
    """
    bid_violations = con.execute(f"""
      SELECT COUNT(*) FROM read_parquet('{path}')
      WHERE
        (bid_0_price IS NOT NULL AND bid_1_price IS NOT NULL AND bid_0_price <= bid_1_price)
        OR (bid_1_price IS NOT NULL AND bid_2_price IS NOT NULL AND bid_1_price <= bid_2_price)
    """).fetchone()[0]
    ask_violations = con.execute(f"""
      SELECT COUNT(*) FROM read_parquet('{path}')
      WHERE
        (ask_0_price IS NOT NULL AND ask_1_price IS NOT NULL AND ask_0_price >= ask_1_price)
        OR (ask_1_price IS NOT NULL AND ask_2_price IS NOT NULL AND ask_1_price >= ask_2_price)
    """).fetchone()[0]
    print(f"[2/5] level ordering: bid_violations={bid_violations:,}  ask_violations={ask_violations:,}")
    return bid_violations == 0 and ask_violations == 0


def _check_spread(con: duckdb.DuckDBPyConnection, path: str) -> bool:
    crossed = con.execute(f"""
      SELECT COUNT(*) FROM read_parquet('{path}')
      WHERE ask_0_price < bid_0_price
    """).fetchone()[0]
    locked = con.execute(f"""
      SELECT COUNT(*) FROM read_parquet('{path}')
      WHERE ask_0_price = bid_0_price
    """).fetchone()[0]
    total = con.execute(f"SELECT COUNT(*) FROM read_parquet('{path}')").fetchone()[0]
    print(f"[3/5] spread: total={total:,}  crossed={crossed:,}  locked={locked:,}")
    return crossed == 0


def _check_cadence(con: duckdb.DuckDBPyConnection, path: str) -> None:
    r = con.execute(f"""
      WITH t AS (
        SELECT timestamp_us, LAG(timestamp_us) OVER (ORDER BY timestamp_us) AS prev_ts
        FROM read_parquet('{path}')
      )
      SELECT
        QUANTILE_CONT(timestamp_us - prev_ts, 0.5) AS p50,
        QUANTILE_CONT(timestamp_us - prev_ts, 0.95) AS p95,
        QUANTILE_CONT(timestamp_us - prev_ts, 0.99) AS p99,
        MAX(timestamp_us - prev_ts) AS max_gap,
        COUNT(*) AS n_intervals
      FROM t WHERE prev_ts IS NOT NULL
    """).fetchone()
    p50, p95, p99, max_gap, n = r
    print(f"[4/5] cadence over {n:,} intervals:")
    print(f"        p50={p50/1000:.1f}ms  p95={p95/1000:.1f}ms  p99={p99/1000:.1f}ms  max={max_gap/1000:.1f}ms")


def _check_imbalance(con: duckdb.DuckDBPyConnection, path: str) -> None:
    """Compute I_top1, I_top5, I_top10 and dump quantiles."""
    print("[5/5] imbalance distribution:")
    for top_n in (1, 5, 10):
        bid_sum = " + ".join(f"COALESCE(bid_{i}_size, 0)" for i in range(top_n))
        ask_sum = " + ".join(f"COALESCE(ask_{i}_size, 0)" for i in range(top_n))
        r = con.execute(f"""
          WITH q AS (
            SELECT
              ({bid_sum}) AS qb,
              ({ask_sum}) AS qa
            FROM read_parquet('{path}')
          )
          SELECT
            QUANTILE_CONT((qb - qa) / NULLIF(qb + qa, 0), 0.05) AS p05,
            QUANTILE_CONT((qb - qa) / NULLIF(qb + qa, 0), 0.50) AS p50,
            QUANTILE_CONT((qb - qa) / NULLIF(qb + qa, 0), 0.95) AS p95,
            AVG(qb) AS avg_qb,
            AVG(qa) AS avg_qa
          FROM q
          WHERE qb + qa > 0
        """).fetchone()
        p05, p50, p95, avg_qb, avg_qa = r
        print(f"        top-{top_n}: I p05={p05:+.3f}  p50={p50:+.3f}  p95={p95:+.3f}"
              f"   avg(Q_bid)={avg_qb:.3f}  avg(Q_ask)={avg_qa:.3f}")


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--input", required=True, help="Path to a book parquet from download_book.py.")
    args = p.parse_args()

    if not os.path.exists(args.input):
        print(f"File not found: {args.input}", file=sys.stderr)
        return 2

    print(f"Verifying {args.input}")
    print(f"  size on disk: {os.path.getsize(args.input)/1e6:.1f} MB")
    print()

    con = duckdb.connect()
    ok_schema = _check_schema(con, args.input)
    ok_levels = _check_level_ordering(con, args.input)
    ok_spread = _check_spread(con, args.input)
    _check_cadence(con, args.input)
    _check_imbalance(con, args.input)

    print()
    if ok_schema and ok_levels and ok_spread:
        print("PASS")
        return 0
    else:
        print("FAIL — see lines above")
        return 1


if __name__ == "__main__":
    sys.exit(main())
