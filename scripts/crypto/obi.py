"""Order-book imbalance (OBI), volume-weighted with exponential distance decay.

Formula:
    OBI_top_N = (sum_i w_i * bid_size_i - sum_i w_i * ask_size_i)
              / (sum_i w_i * bid_size_i + sum_i w_i * ask_size_i)
    w_i = exp(-lambda * |price_i - mid| / tick_size)

Output is in [-1, +1]. +1 means all weighted size is on the bid (buy pressure),
-1 means all weighted size is on the ask. The exponential tilt penalises levels
far from the inside, on the assumption that deep liquidity rarely fills.

Two reconstruction paths are supported:
  - obi_from_book:        consume Crypto Lake `book` snapshots (already 20-deep)
  - obi_from_delta:       seed from a snapshot, replay book_delta_v2 events,
                          resample at a fixed cadence

Both return a (timestamps_us, obi_series) pair so the chart can overlay them
on the same time axis.
"""
from __future__ import annotations

import numpy as np
import pandas as pd


def _exponential_weights(depth: int, lambda_decay: float) -> np.ndarray:
    """Pre-computed weights w_i = exp(-lambda * i) for i in 0..depth-1.

    We use the level *index* as the distance proxy rather than the actual
    |price - mid| / tick_size. On BTC perps the inside spread is essentially
    always 1 tick and the levels we care about (top-5, top-10) are densely
    populated, so level i sits at ~i ticks from inside on the same side.
    Computing weights from indexes is exact under that assumption and avoids
    a per-row distance recompute.
    """
    return np.exp(-lambda_decay * np.arange(depth, dtype=np.float64))


def obi_from_book(
    book_df: pd.DataFrame, depth: int, lambda_decay: float
) -> tuple[np.ndarray, np.ndarray]:
    """Compute OBI series from Crypto Lake `book` snapshots.

    Each row of `book_df` carries the top-20 levels per side as
    `bid_{i}_price`, `bid_{i}_size`, `ask_{i}_price`, `ask_{i}_size` for
    i in 0..19. We consume the first `depth` levels and apply exponential
    distance weights to size (price columns are not needed under the
    index-distance approximation, see `_exponential_weights`).

    Returns (timestamps_us, obi) as parallel int64/float64 arrays.
    """
    if depth < 1 or depth > 20:
        raise ValueError(f"depth must be in 1..20, got {depth}")

    w = _exponential_weights(depth, lambda_decay)
    bid_sizes = np.stack(
        [book_df[f"bid_{i}_size"].to_numpy(dtype=np.float64) for i in range(depth)],
        axis=1,
    )
    ask_sizes = np.stack(
        [book_df[f"ask_{i}_size"].to_numpy(dtype=np.float64) for i in range(depth)],
        axis=1,
    )
    # NaNs (missing levels) treated as zero size.
    np.nan_to_num(bid_sizes, copy=False)
    np.nan_to_num(ask_sizes, copy=False)

    qb = bid_sizes @ w
    qa = ask_sizes @ w
    denom = qb + qa
    # Guard against the rare empty-book row.
    obi = np.where(denom > 0, (qb - qa) / np.maximum(denom, 1e-12), 0.0)

    return book_df["timestamp_us"].to_numpy(dtype=np.int64), obi


def replay_deltas(
    seed_book_row: pd.Series, delta_df: pd.DataFrame, t_end_us: int,
    sample_cadence_us: int,
) -> tuple[np.ndarray, dict, dict]:
    """Replay book_delta_v2 events on top of a seed snapshot. Yield the live
    book state sampled at fixed cadence.

    Args:
        seed_book_row: a single row from `book` parquet — the snapshot at t_seed.
        delta_df: book_delta_v2 frame, ordered by timestamp_us, restricted to
            rows with timestamp_us >= seed_t_us.
        t_end_us: stop replay at this timestamp.
        sample_cadence_us: resample every this many microseconds (e.g. 100_000
            for 100 ms).

    Returns:
        (sample_times_us, bid_levels_at_each_sample, ask_levels_at_each_sample)
        where bid_levels and ask_levels are lists of dicts (price -> size) at
        each sample time. We use sorted dicts (Python 3.7+ preserves insertion
        order; we keep them sorted manually after each batch of mutations).

    We do NOT keep an in-memory copy of every level state per sample; instead
    we yield via a generator pattern at the OBI computation layer to avoid
    quadratic memory. See `obi_from_delta` which is the proper public API.
    """
    raise NotImplementedError("call obi_from_delta directly; this helper is"
                              " unused and kept only as a doc placeholder")


def obi_from_delta(
    seed_book_row: pd.Series,
    delta_df: pd.DataFrame,
    depth: int,
    lambda_decay: float,
    sample_cadence_us: int,
    t_end_us: int,
) -> tuple[np.ndarray, np.ndarray]:
    """Replay book_delta_v2 events on top of a seed snapshot, sample the live
    book at fixed cadence, and compute OBI on each sample.

    Args:
        seed_book_row: pd.Series with the 80 level columns + `timestamp_us`,
            taken from the `book` parquet at some point in the day. The
            replay starts here.
        delta_df: book_delta_v2 frame for the same day. Must have columns
            `timestamp_us`, `side_is_bid`, `price`, `size`. Filtered to
            `timestamp_us >= seed.timestamp_us` and ordered by timestamp_us.
        depth: top-N levels to use for OBI.
        lambda_decay: exponential weight decay (per level index).
        sample_cadence_us: resample every this many microseconds.
        t_end_us: stop replay at this timestamp (typically end of day).

    Returns (sample_times_us, obi) parallel int64/float64 arrays.
    """
    if depth < 1:
        raise ValueError(f"depth must be >= 1, got {depth}")

    w = _exponential_weights(depth, lambda_decay)

    # Seed the live book from the snapshot row.
    bid_book: dict[float, float] = {}
    ask_book: dict[float, float] = {}
    seed_t_us = int(seed_book_row["timestamp_us"])
    for i in range(20):
        bp = float(seed_book_row.get(f"bid_{i}_price", float("nan")))
        bs = float(seed_book_row.get(f"bid_{i}_size", 0.0))
        if not np.isnan(bp) and bs > 0:
            bid_book[bp] = bs
        ap = float(seed_book_row.get(f"ask_{i}_price", float("nan")))
        as_ = float(seed_book_row.get(f"ask_{i}_size", 0.0))
        if not np.isnan(ap) and as_ > 0:
            ask_book[ap] = as_

    # Restrict deltas to t >= seed.
    delta_df = delta_df[delta_df["timestamp_us"] >= seed_t_us]

    ts_arr = delta_df["timestamp_us"].to_numpy(dtype=np.int64)
    side_arr = delta_df["side_is_bid"].to_numpy(dtype=bool)
    price_arr = delta_df["price"].to_numpy(dtype=np.float64)
    size_arr = delta_df["size"].to_numpy(dtype=np.float64)
    n = len(ts_arr)

    sample_times: list[int] = []
    obi_vals: list[float] = []

    next_sample_t = seed_t_us + sample_cadence_us
    i = 0
    while i < n and ts_arr[i] <= t_end_us:
        t = int(ts_arr[i])
        # Apply all events before the next sample boundary.
        while i < n and ts_arr[i] < next_sample_t:
            p = float(price_arr[i])
            s = float(size_arr[i])
            if side_arr[i]:
                if s == 0.0:
                    bid_book.pop(p, None)
                else:
                    bid_book[p] = s
            else:
                if s == 0.0:
                    ask_book.pop(p, None)
                else:
                    ask_book[p] = s
            i += 1

        # Snapshot the live book at next_sample_t.
        if next_sample_t > t_end_us:
            break
        # Top-N bids: highest prices first.
        top_bids = sorted(bid_book.items(), key=lambda kv: -kv[0])[:depth]
        top_asks = sorted(ask_book.items(), key=lambda kv: kv[0])[:depth]
        qb = sum(w[k] * top_bids[k][1] for k in range(len(top_bids)))
        qa = sum(w[k] * top_asks[k][1] for k in range(len(top_asks)))
        denom = qb + qa
        obi = (qb - qa) / denom if denom > 0 else 0.0
        sample_times.append(next_sample_t)
        obi_vals.append(obi)
        next_sample_t += sample_cadence_us

    return np.asarray(sample_times, dtype=np.int64), np.asarray(obi_vals, dtype=np.float64)
