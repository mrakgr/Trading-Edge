"""PyTorch Dataset for the hold-detector pipeline.

Reads CDF-transformed hold-dataset parquets (one row group per day, variable
bars per day) produced by `dotnet ... apply-hold-cdf`. Each sample is a
`window_size`-bar window of the four CDF features; the label is the `is_hold`
flag of the bar at `pos + target_offset` within the window.

Companion to TradingEdge.Simulation/HoldDigests.fs (cdfSchema):

  day_id: int, bar_idx: int,
  cdf_rel_stddev: float, cdf_ret: float, cdf_duration: float,
  cdf_trade_count: float,
  is_hold: int

The original scripts/training/dataset.py pulled three resolutions of CDF'd
OHLC features at a fixed 23,400 bars/day. This dataset is the simpler
single-resolution / variable-length analogue for v0.
"""

from __future__ import annotations

import bisect
from typing import Optional

import numpy as np
import pyarrow.parquet as pq
import torch
from torch.utils.data import Dataset, Sampler


FEATURE_COLUMNS = (
    "cdf_rel_stddev",
    "cdf_ret",
    "cdf_duration",
    "cdf_trade_count",
)


class HoldDataset(Dataset):
    """Windowed bar sequences from a CDF-transformed hold-dataset parquet.

    Args:
        parquet_path: path to a parquet written by HoldDigests.cdfSchema.
        window_size: number of consecutive bars per sample.
        target_offset: index within the window whose `is_hold` becomes the
            label. Defaults to the middle of the window — non-causal, fine
            for offline mining (use window_size - 1 for live).
        stride: step between window starts. 1 = every bar.
        day_filter: optional (lo, hi) day_id range, half-open [lo, hi). Use
            for train/test splits without writing two parquets.
    """

    def __init__(
        self,
        parquet_path: str,
        window_size: int = 60,
        target_offset: Optional[int] = None,
        stride: int = 1,
        day_filter: Optional[tuple[int, int]] = None,
    ):
        if target_offset is None:
            target_offset = window_size // 2
        if not (0 <= target_offset < window_size):
            raise ValueError(f"target_offset {target_offset} not in [0, {window_size})")

        self.parquet_path = parquet_path
        self.window_size = window_size
        self.target_offset = target_offset
        self.stride = stride

        self.pf = pq.ParquetFile(parquet_path)
        n_groups = self.pf.metadata.num_row_groups

        # Discover row groups + their bar counts. Apply day_filter via the
        # day_id of the first bar in each row group — the F# writer puts one
        # day per row group, so this is unambiguous.
        self.row_groups: list[int] = []
        self.bars_per_group: list[int] = []
        self.windows_per_group: list[int] = []
        for gi in range(n_groups):
            md = self.pf.metadata.row_group(gi)
            n_bars = md.num_rows
            if day_filter is not None:
                # Cheaper than reading the whole group: read just the first row
                # of day_id from this group's column statistics.
                day_col_idx = self.pf.schema_arrow.get_field_index("day_id")
                stats = md.column(day_col_idx).statistics
                first_day = int(stats.min) if stats and stats.has_min_max else None
                if first_day is None:
                    # Fallback: read the column.
                    first_day = int(self.pf.read_row_group(gi, columns=["day_id"]).column("day_id")[0].as_py())
                lo, hi = day_filter
                if not (lo <= first_day < hi):
                    continue
            n_windows = max(0, (n_bars - window_size) // stride + 1)
            if n_windows == 0:
                continue
            self.row_groups.append(gi)
            self.bars_per_group.append(n_bars)
            self.windows_per_group.append(n_windows)

        # Cumulative window counts for fast (idx -> group, offset) mapping.
        self.cum_windows = np.cumsum(self.windows_per_group).tolist()
        self._total_windows = self.cum_windows[-1] if self.cum_windows else 0

        # One-row-group cache: __getitem__ in a row-group-major sampler hits
        # this consistently. Mirrors the strategy in the older dataset.py.
        self._cache_idx: int = -1
        self._cache_arrays: dict[str, np.ndarray] = {}

    def __len__(self) -> int:
        return self._total_windows

    def _locate(self, idx: int) -> tuple[int, int]:
        """Map a flat sample index to (row_group, window_offset)."""
        if not (0 <= idx < self._total_windows):
            raise IndexError(idx)
        gi = bisect.bisect_right(self.cum_windows, idx)
        prev = self.cum_windows[gi - 1] if gi > 0 else 0
        within = idx - prev
        return self.row_groups[gi], within * self.stride

    def _load_group(self, group_idx: int) -> dict[str, np.ndarray]:
        if self._cache_idx != group_idx:
            tbl = self.pf.read_row_group(group_idx, columns=list(FEATURE_COLUMNS) + ["is_hold"])
            self._cache_arrays = {
                name: tbl.column(name).to_numpy(zero_copy_only=False)
                for name in FEATURE_COLUMNS + ("is_hold",)
            }
            self._cache_idx = group_idx
        return self._cache_arrays

    def __getitem__(self, idx: int) -> dict:
        group_idx, pos = self._locate(idx)
        arrs = self._load_group(group_idx)
        end = pos + self.window_size

        # Stack the four feature columns into a (window_size, 4) tensor.
        features = np.stack([arrs[c][pos:end] for c in FEATURE_COLUMNS], axis=1).astype(np.float32, copy=False)
        label = int(arrs["is_hold"][pos + self.target_offset])
        return {
            "features": torch.from_numpy(features),
            "label": torch.tensor(label, dtype=torch.float32),
        }


class RowGroupSampler(Sampler):
    """Shuffle row groups, iterate sequentially within each.

    Mirrors scripts/training/dataset.py:RowGroupSampler. Keeps the per-group
    cache hot while still randomizing day order across epochs.
    """

    def __init__(self, dataset: HoldDataset, shuffle: bool = True, seed: Optional[int] = None):
        self.dataset = dataset
        self.shuffle = shuffle
        self.seed = seed

    def __iter__(self):
        n = len(self.dataset.row_groups)
        order = list(range(n))
        if self.shuffle:
            rng = np.random.default_rng(self.seed)
            rng.shuffle(order)
        for gi in order:
            base = sum(self.dataset.windows_per_group[:gi])
            for offset in range(self.dataset.windows_per_group[gi]):
                yield base + offset

    def __len__(self):
        return len(self.dataset)
