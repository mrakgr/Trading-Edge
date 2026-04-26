#!/usr/bin/env python3
"""Run a trained HoldGMLP over a CDF'd hold-dataset parquet and write per-bar
hold probabilities back as a parquet alongside `day_id` / `bar_idx` so it can
be joined onto the original bar data for visualization.

Each emitted bar's probability comes from the window centered on it (or
wherever the checkpoint's `target_offset` lands the prediction). Bars at the
day boundaries — within the first `target_offset` or the last
`window_size - target_offset - 1` — get NaN since no full window is available.
"""

from __future__ import annotations

import argparse
import os
import time

import numpy as np
import pyarrow as pa
import pyarrow.parquet as pq
import torch
from torch.utils.data import DataLoader

from hold_dataset import FEATURE_COLUMNS, HoldDataset, RowGroupSampler
from train_hold_gmlp import HoldGMLP


def load_checkpoint(path: str, device: torch.device) -> tuple[HoldGMLP, dict]:
    blob = torch.load(path, map_location=device)
    if "config" not in blob or "state_dict" not in blob:
        raise ValueError(f"{path} is not a hold-gmlp checkpoint (missing config/state_dict)")
    cfg = blob["config"]
    model = HoldGMLP(
        seq_len=cfg["seq_len"],
        input_channels=cfg["input_channels"],
        hidden_dim=cfg["hidden_dim"],
        ffn_dim=cfg["ffn_dim"],
        num_layers=cfg["num_layers"],
    ).to(device)
    model.load_state_dict(blob["state_dict"])
    model.eval()
    return model, cfg


@torch.no_grad()
def predict(model: HoldGMLP, dataset: HoldDataset, batch_size: int, device: torch.device) -> dict[int, np.ndarray]:
    """Run inference and return {row_group_index: per-bar prob array}.

    Per-bar prob array has length = bars_in_day, with NaN at edges where no
    window fits.
    """
    sampler = RowGroupSampler(dataset, shuffle=False)
    loader = DataLoader(dataset, batch_size=batch_size, sampler=sampler, num_workers=0)

    target_offset = dataset.target_offset
    window = dataset.window_size

    # Pre-allocate per-group prob arrays filled with NaN.
    probs_by_group: dict[int, np.ndarray] = {
        gi: np.full(n_bars, np.nan, dtype=np.float32)
        for gi, n_bars in zip(dataset.row_groups, dataset.bars_per_group)
    }

    # Walk the sampler in lockstep with the DataLoader to know which (group, pos)
    # each prediction corresponds to. The sampler yields global window indices
    # in row-group-major order; we map them back via dataset._locate.
    iter_indices = iter(sampler)
    start = time.time()
    n_done = 0

    for batch in loader:
        x = batch["features"].to(device)
        logits = model(x)
        probs = torch.sigmoid(logits).cpu().numpy()
        for prob in probs:
            flat_idx = next(iter_indices)
            gi, pos = dataset._locate(flat_idx)
            probs_by_group[gi][pos + target_offset] = prob
        n_done += len(probs)
        if n_done % (batch_size * 50) == 0:
            elapsed = time.time() - start
            sps = n_done / elapsed if elapsed > 0 else 0
            print(f"  predicted {n_done:,} windows  ({sps:.0f} samples/sec)")

    return probs_by_group


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--checkpoint", required=True, help="Trained model checkpoint from train_hold_gmlp.py")
    p.add_argument("--input", required=True, help="CDF'd hold-dataset parquet to predict on")
    p.add_argument("--output", required=True, help="Output parquet (day_id, bar_idx, hold_prob)")
    p.add_argument("--stride", type=int, default=1)
    p.add_argument("--batch-size", type=int, default=512)
    args = p.parse_args()

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Device: {device}")

    model, cfg = load_checkpoint(args.checkpoint, device)
    print(f"Loaded checkpoint with config: {cfg}")

    ds = HoldDataset(
        args.input,
        window_size=cfg["seq_len"],
        target_offset=cfg["target_offset"],
        stride=args.stride,
    )
    print(f"Input has {len(ds.row_groups)} day(s), {sum(ds.bars_per_group):,} bars total, {len(ds):,} prediction windows")

    probs_by_group = predict(model, ds, args.batch_size, device)

    # Pull day_id / bar_idx alongside probs for the output parquet so callers
    # can join back to the original bar data without re-deriving keys.
    pf = pq.ParquetFile(args.input)
    day_ids_out: list[int] = []
    bar_idx_out: list[int] = []
    probs_out: list[float] = []
    for gi in ds.row_groups:
        keys = pf.read_row_group(gi, columns=["day_id", "bar_idx"]).to_pandas()
        day_ids_out.extend(keys["day_id"].tolist())
        bar_idx_out.extend(keys["bar_idx"].tolist())
        probs_out.extend(probs_by_group[gi].tolist())

    table = pa.table({
        "day_id": pa.array(day_ids_out, type=pa.int32()),
        "bar_idx": pa.array(bar_idx_out, type=pa.int32()),
        "hold_prob": pa.array(probs_out, type=pa.float32()),
    })
    os.makedirs(os.path.dirname(args.output) or ".", exist_ok=True)
    pq.write_table(table, args.output)
    n_valid = sum(1 for p in probs_out if not np.isnan(p))
    print(f"Wrote {len(probs_out):,} rows ({n_valid:,} with valid prob) to {args.output}")


if __name__ == "__main__":
    main()
