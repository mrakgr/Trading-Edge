#!/usr/bin/env python3
"""Run a trained HoldGMLP over a CDF'd hold-dataset parquet and write per-bar
class probabilities back as a parquet alongside `day_id` / `bar_idx` so it can
be joined onto the original bar data for visualization.

Each emitted bar's probability vector comes from the window centered on it
(or wherever the checkpoint's `target_offset` lands the prediction). With
bilateral zero-padding every bar in the day is a valid target, so the output
covers the entire day; bars near the boundary just see partially-padded
windows and the model relies on the mask channel to tell real from padded.

Output schema:
  day_id: int, bar_idx: int,
  pred_label: int (argmax class),
  prob_<i>: float for i in 0..num_classes-1

Stride > 1 leaves NaN at non-stride positions and pred_label = -1 there.
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

from hold_dataset import HoldDataset, RowGroupSampler
from train_hold_gmlp import HoldGMLP


def load_checkpoint(path: str, device: torch.device) -> tuple[HoldGMLP, dict, list[str]]:
    blob = torch.load(path, map_location=device)
    if "config" not in blob or "state_dict" not in blob:
        raise ValueError(f"{path} is not a hold-gmlp checkpoint (missing config/state_dict)")
    cfg = blob["config"]
    label_names = blob.get("label_names", [])
    model = HoldGMLP(
        seq_len=cfg["seq_len"],
        input_channels=cfg["input_channels"],
        hidden_dim=cfg["hidden_dim"],
        ffn_dim=cfg["ffn_dim"],
        num_layers=cfg["num_layers"],
        num_classes=cfg["num_classes"],
    ).to(device)
    model.load_state_dict(blob["state_dict"])
    model.eval()
    return model, cfg, label_names


@torch.no_grad()
def predict(model: HoldGMLP, dataset: HoldDataset, batch_size: int, device: torch.device, num_classes: int) -> dict[int, np.ndarray]:
    """Run inference and return {row_group_index: (n_bars, num_classes) prob array}.
    Bars at non-stride positions stay NaN (no window emitted)."""
    sampler = RowGroupSampler(dataset, shuffle=False)
    loader = DataLoader(dataset, batch_size=batch_size, sampler=sampler, num_workers=0)

    probs_by_group: dict[int, np.ndarray] = {
        gi: np.full((n_bars, num_classes), np.nan, dtype=np.float32)
        for gi, n_bars in zip(dataset.row_groups, dataset.bars_per_group)
    }

    iter_indices = iter(sampler)
    start = time.time()
    n_done = 0

    for batch in loader:
        x = batch["features"].to(device)
        logits = model(x)
        probs = torch.softmax(logits, dim=-1).cpu().numpy()
        for prob_vec in probs:
            flat_idx = next(iter_indices)
            gi, target_pos = dataset._locate(flat_idx)
            probs_by_group[gi][target_pos] = prob_vec
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
    p.add_argument("--output", required=True, help="Output parquet (day_id, bar_idx, pred_label, prob_<i>...)")
    p.add_argument("--stride", type=int, default=1)
    p.add_argument("--batch-size", type=int, default=512)
    args = p.parse_args()

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Device: {device}")

    model, cfg, label_names = load_checkpoint(args.checkpoint, device)
    num_classes = cfg["num_classes"]
    print(f"Loaded checkpoint: {num_classes} classes, seq_len={cfg['seq_len']}, target_offset={cfg['target_offset']}")
    if label_names:
        print("  Classes: " + ", ".join(f"[{i}]{n}" for i, n in enumerate(label_names) if n))

    ds = HoldDataset(
        args.input,
        window_size=cfg["seq_len"],
        target_offset=cfg["target_offset"],
        stride=args.stride,
    )
    print(f"Input has {len(ds.row_groups)} day(s), {sum(ds.bars_per_group):,} bars total, {len(ds):,} prediction windows")

    probs_by_group = predict(model, ds, args.batch_size, device, num_classes)

    pf = pq.ParquetFile(args.input)
    schema_names = set(pf.schema_arrow.names)
    have_ts = {"start_us", "end_us"}.issubset(schema_names)
    have_vol = "volume" in schema_names
    base_cols = ["day_id", "bar_idx"]
    if have_ts:
        base_cols += ["start_us", "end_us"]
    if have_vol:
        base_cols += ["volume"]
    day_ids_out: list[int] = []
    bar_idx_out: list[int] = []
    start_us_out: list[int] = []
    end_us_out: list[int] = []
    volume_out: list[float] = []
    pred_label_out: list[int] = []
    prob_cols: list[list[float]] = [[] for _ in range(num_classes)]
    for gi in ds.row_groups:
        keys = pf.read_row_group(gi, columns=base_cols).to_pandas()
        probs = probs_by_group[gi]                     # (n_bars, num_classes)
        argmax = np.where(np.isnan(probs[:, 0]), -1, np.nanargmax(probs, axis=1))
        day_ids_out.extend(keys["day_id"].tolist())
        bar_idx_out.extend(keys["bar_idx"].tolist())
        if have_ts:
            start_us_out.extend(keys["start_us"].tolist())
            end_us_out.extend(keys["end_us"].tolist())
        if have_vol:
            volume_out.extend(keys["volume"].tolist())
        pred_label_out.extend(argmax.astype(np.int32).tolist())
        for c in range(num_classes):
            prob_cols[c].extend(probs[:, c].tolist())

    table_dict: dict = {
        "day_id": pa.array(day_ids_out, type=pa.int32()),
        "bar_idx": pa.array(bar_idx_out, type=pa.int32()),
        "pred_label": pa.array(pred_label_out, type=pa.int32()),
    }
    if have_ts:
        table_dict["start_us"] = pa.array(start_us_out, type=pa.int64())
        table_dict["end_us"] = pa.array(end_us_out, type=pa.int64())
    if have_vol:
        table_dict["volume"] = pa.array(volume_out, type=pa.float64())
    for c in range(num_classes):
        table_dict[f"prob_{c}"] = pa.array(prob_cols[c], type=pa.float32())
    table = pa.table(table_dict)

    os.makedirs(os.path.dirname(args.output) or ".", exist_ok=True)
    pq.write_table(table, args.output)
    n_valid = sum(1 for p in pred_label_out if p >= 0)
    print(f"Wrote {len(pred_label_out):,} rows ({n_valid:,} with valid pred) to {args.output}")


if __name__ == "__main__":
    main()
