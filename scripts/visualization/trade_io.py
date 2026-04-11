"""Shared Parquet reader for trade data.

Visualization scripts used to call ``json.load(open(path))`` on per-ticker-day
JSON files. After the Parquet migration, all trades live on disk as zstd
compressed Parquet with a trimmed 5-column schema. This module provides a
drop-in ``load_trades`` that returns the same dict-shaped rows the scripts
already know how to consume, so the rest of each script stays unchanged.

Fields kept in the Parquet file (and returned here):
    participant_timestamp  int64   ns since Unix epoch, may be 0 (OTC)
    sip_timestamp          int64   ns since Unix epoch
    price                  float
    size                   float
    conditions             list[int] | None

Fields dropped on migration (never read downstream):
    id, exchange, sequence_number, tape
"""

from __future__ import annotations

import duckdb


def load_trades(path: str) -> list[dict]:
    """Load a trades Parquet file into a list of dicts.

    Rows come back sorted by participant_timestamp (falling back to
    sip_timestamp when participant_timestamp is 0, mirroring the F# reader's
    convention). The ``size`` column is cast to int to match the shape the
    visualization scripts historically received from the JSON format.
    """
    conn = duckdb.connect(":memory:")
    rows = conn.execute(
        """
        SELECT participant_timestamp, sip_timestamp, price, size, conditions
        FROM read_parquet(?)
        ORDER BY
            CASE WHEN participant_timestamp <> 0
                 THEN participant_timestamp
                 ELSE sip_timestamp
            END
        """,
        [path],
    ).fetchall()
    conn.close()
    return [
        {
            "participant_timestamp": r[0],
            "sip_timestamp": r[1],
            "price": r[2],
            "size": int(r[3]),
            "conditions": list(r[4]) if r[4] is not None else None,
        }
        for r in rows
    ]
