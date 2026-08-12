"""Shared helpers for the FlushFader analysis tools.

Import from a sibling script with:

    import os, sys
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from flushfader_common import raw_px_expr
"""


def raw_px_expr(con, trips):
    """The SQL expression giving a trip's entry price in RAW dollars.

    ⭐ S43br split the trip schema in two, and the $1 book floor has to follow:

      v43_legtick and earlier — the 1s tape was scaled by `adj_ratio` in
        SecEmitter, so `entry_px` is an ADJUSTED price and the raw one is
        `entry_px/adj_ratio`. That ratio is itself the lookahead the migration
        removed (adj_ratio folds in every FUTURE split).

      v44_causal and later — the tape is RAW, so `entry_px` IS the raw price and
        the floor is a plain comparison. No ratio of two differently scaled
        numbers, which is the entire point.

    Detected from the columns so both schemas stay readable and a control run can
    compare them directly. Returns (expr, human_label).
    """
    cols = {r[0] for r in con.execute(
        f"DESCRIBE SELECT * FROM read_parquet('{trips}')").fetchall()}
    if "adj_ratio" in cols:
        return "entry_px/adj_ratio", "legacy (adjusted tape)"
    if "n" in cols:
        return "entry_px", "causal (raw tape)"
    raise SystemExit(
        f"{trips}: trips carry neither `adj_ratio` nor `n` — unrecognised schema")
