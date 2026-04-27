#!/usr/bin/env python3
"""gMLP training for the binary hold-vs-not detector.

Reuses SpatialGatingUnit / GMLPBlock from train_gmlp.py (the multi-resolution
session/trend classifier) — only the input shape (256 patches × 5 features) and
the head (single binary logit, BCE) are new for this task.

Run end-to-end:

    python scripts/training/train_hold_gmlp.py \
        --train data/synth_cdf.parquet --train-days 0:9000 \
        --test  data/synth_cdf.parquet --test-days 9000:10000 \
        --epochs 5 --checkpoint data/hold_gmlp.pt
"""

from __future__ import annotations

import argparse
import time
from typing import Optional

import torch
import torch.nn as nn
from torch.utils.data import DataLoader

from hold_dataset import NUM_INPUT_CHANNELS, HoldDataset, RowGroupSampler


# =============================================================================
# Architecture (reuses the gMLP block from train_gmlp.py, single resolution)
# =============================================================================


class SpatialGatingUnit(nn.Module):
    """Spatial Gating Unit: s(Z) = Z1 * f(Z2). f mixes across the sequence."""

    def __init__(self, d_ffn: int, seq_len: int):
        super().__init__()
        self.norm = nn.LayerNorm(d_ffn // 2)
        self.weight = nn.Parameter(torch.zeros(seq_len, seq_len).uniform_(-0.01, 0.01))
        self.bias = nn.Parameter(torch.ones(seq_len))

    def forward(self, z):
        z1, z2 = z.chunk(2, dim=-1)
        z2 = self.norm(z2)
        z2 = torch.einsum("ij,bjd->bid", self.weight, z2) + self.bias.unsqueeze(-1)
        return z1 * z2


class GMLPBlock(nn.Module):
    def __init__(self, d_model: int, d_ffn: int, seq_len: int):
        super().__init__()
        self.norm = nn.LayerNorm(d_model)
        self.proj1 = nn.Linear(d_model, d_ffn)
        self.activation = nn.GELU()
        self.sgu = SpatialGatingUnit(d_ffn, seq_len)
        self.proj2 = nn.Linear(d_ffn // 2, d_model)

    def forward(self, x):
        shortcut = x
        x = self.norm(x)
        z = self.activation(self.proj1(x))
        z = self.sgu(z)
        return self.proj2(z) + shortcut


class HoldGMLP(nn.Module):
    """Single-resolution binary classifier. Predicts hold-or-not on the bar
    at `target_offset` of the input window (HoldDataset handles the labeling)."""

    def __init__(
        self,
        seq_len: int = 256,
        input_channels: int = NUM_INPUT_CHANNELS,
        hidden_dim: int = 128,
        ffn_dim: int = 512,
        num_layers: int = 4,
    ):
        super().__init__()
        self.seq_len = seq_len
        self.input_channels = input_channels
        self.hidden_dim = hidden_dim
        self.ffn_dim = ffn_dim
        self.num_layers = num_layers

        self.embed = nn.Linear(input_channels, hidden_dim)
        self.blocks = nn.Sequential(*[GMLPBlock(hidden_dim, ffn_dim, seq_len) for _ in range(num_layers)])
        self.norm = nn.LayerNorm(hidden_dim)
        self.head = nn.Linear(hidden_dim, 1)

    def forward(self, x):  # x: (batch, seq_len, input_channels)
        x = self.embed(x)
        x = self.blocks(x)
        x = self.norm(x.mean(dim=1))
        return self.head(x).squeeze(-1)  # (batch,)


# =============================================================================
# Train / eval loops
# =============================================================================


def _confusion(logits: torch.Tensor, labels: torch.Tensor, threshold: float = 0.0):
    """Return (tp, fp, fn, tn) at the given logit threshold (default 0 = p>0.5)."""
    pred = (logits > threshold).int()
    label = labels.int()
    tp = ((pred == 1) & (label == 1)).sum().item()
    fp = ((pred == 1) & (label == 0)).sum().item()
    fn = ((pred == 0) & (label == 1)).sum().item()
    tn = ((pred == 0) & (label == 0)).sum().item()
    return tp, fp, fn, tn


def _metrics(tp: int, fp: int, fn: int, tn: int):
    total = tp + fp + fn + tn
    acc = (tp + tn) / total if total else 0.0
    precision = tp / (tp + fp) if (tp + fp) else 0.0
    recall = tp / (tp + fn) if (tp + fn) else 0.0
    f1 = 2 * precision * recall / (precision + recall) if (precision + recall) else 0.0
    return acc, precision, recall, f1


def train_epoch(model, loader, optimizer, device, log_every: int = 100):
    model.train()
    criterion = nn.BCEWithLogitsLoss()
    tp = fp = fn = tn = 0
    total_loss = 0.0
    n_batches = 0
    start = time.time()

    for batch_idx, batch in enumerate(loader):
        x = batch["features"].to(device)
        y = batch["label"].to(device)
        optimizer.zero_grad()
        logits = model(x)
        loss = criterion(logits, y)
        loss.backward()
        optimizer.step()

        total_loss += loss.item()
        n_batches += 1
        b_tp, b_fp, b_fn, b_tn = _confusion(logits.detach(), y)
        tp += b_tp; fp += b_fp; fn += b_fn; tn += b_tn

        if batch_idx % log_every == 0:
            elapsed = time.time() - start
            samples = (tp + fp + fn + tn)
            sps = samples / elapsed if elapsed > 0 else 0
            print(f"  Batch {batch_idx}: loss={loss.item():.4f}, {sps:.0f} samples/sec")

    acc, p, r, f1 = _metrics(tp, fp, fn, tn)
    return {
        "loss": total_loss / max(1, n_batches),
        "acc": acc, "precision": p, "recall": r, "f1": f1,
        "pos_rate": (tp + fn) / max(1, tp + fp + fn + tn),
        "time": time.time() - start,
    }


@torch.no_grad()
def evaluate(model, loader, device):
    model.eval()
    criterion = nn.BCEWithLogitsLoss()
    tp = fp = fn = tn = 0
    total_loss = 0.0
    n_batches = 0
    start = time.time()
    for batch in loader:
        x = batch["features"].to(device)
        y = batch["label"].to(device)
        logits = model(x)
        loss = criterion(logits, y)
        total_loss += loss.item(); n_batches += 1
        b_tp, b_fp, b_fn, b_tn = _confusion(logits, y)
        tp += b_tp; fp += b_fp; fn += b_fn; tn += b_tn
    acc, p, r, f1 = _metrics(tp, fp, fn, tn)
    return {
        "loss": total_loss / max(1, n_batches),
        "acc": acc, "precision": p, "recall": r, "f1": f1,
        "pos_rate": (tp + fn) / max(1, tp + fp + fn + tn),
        "time": time.time() - start,
    }


# =============================================================================
# CLI
# =============================================================================


def _parse_day_range(spec: Optional[str]) -> Optional[tuple[int, int]]:
    if spec is None:
        return None
    lo, _, hi = spec.partition(":")
    if not hi:
        raise ValueError(f"--train-days/--test-days expects 'lo:hi', got {spec!r}")
    return int(lo), int(hi)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--train", required=True, help="Training parquet (CDF-transformed hold dataset)")
    p.add_argument("--test", required=True, help="Test parquet")
    p.add_argument("--train-days", default=None, help="Day range 'lo:hi' (half-open) for train")
    p.add_argument("--test-days", default=None, help="Day range 'lo:hi' (half-open) for test")
    p.add_argument("--window-size", type=int, default=256)
    p.add_argument("--target-offset", type=int, default=None, help="Bar within window to predict (default: middle)")
    p.add_argument("--stride", type=int, default=4)
    p.add_argument("--batch-size", type=int, default=256)
    p.add_argument("--epochs", type=int, default=1)
    p.add_argument("--lr", type=float, default=1e-3)
    p.add_argument("--weight-decay", type=float, default=0.0, help="AdamW weight decay")
    p.add_argument("--checkpoint", default="data/hold_gmlp.pt")
    p.add_argument("--num-workers", type=int, default=0, help="DataLoader workers (0 = main thread; row-group cache works best)")
    args = p.parse_args()

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Device: {device}")

    train_filter = _parse_day_range(args.train_days)
    test_filter = _parse_day_range(args.test_days)

    train_ds = HoldDataset(
        args.train, window_size=args.window_size, target_offset=args.target_offset,
        stride=args.stride, day_filter=train_filter,
    )
    test_ds = HoldDataset(
        args.test, window_size=args.window_size, target_offset=args.target_offset,
        stride=args.stride, day_filter=test_filter,
    )
    print(f"Train windows: {len(train_ds):,}  ({len(train_ds.row_groups)} days)")
    print(f"Test  windows: {len(test_ds):,}  ({len(test_ds.row_groups)} days)")

    train_loader = DataLoader(
        train_ds, batch_size=args.batch_size,
        sampler=RowGroupSampler(train_ds, shuffle=True),
        num_workers=args.num_workers,
    )
    test_loader = DataLoader(
        test_ds, batch_size=args.batch_size,
        sampler=RowGroupSampler(test_ds, shuffle=False),
        num_workers=args.num_workers,
    )

    model = HoldGMLP(seq_len=args.window_size).to(device)
    n_params = sum(p.numel() for p in model.parameters())
    print(f"Model: HoldGMLP, {n_params:,} parameters")

    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=args.weight_decay)

    import os
    os.makedirs(os.path.dirname(args.checkpoint) or ".", exist_ok=True)

    def save_checkpoint(path: str, epoch: int, test_loss: float):
        torch.save({
            "state_dict": model.state_dict(),
            "config": {
                "seq_len": args.window_size,
                "input_channels": NUM_INPUT_CHANNELS,
                "hidden_dim": model.hidden_dim,
                "ffn_dim": model.ffn_dim,
                "num_layers": model.num_layers,
                "target_offset": args.target_offset if args.target_offset is not None else args.window_size // 2,
            },
            "epoch": epoch,
            "test_loss": test_loss,
        }, path)

    best_test_loss = float("inf")
    best_epoch = -1
    total_start = time.time()
    for epoch in range(args.epochs):
        print(f"\n=== Epoch {epoch+1}/{args.epochs} ===")
        tr = train_epoch(model, train_loader, optimizer, device)
        print(f"Train  loss={tr['loss']:.4f}  acc={tr['acc']:.4f}  P={tr['precision']:.4f}  "
              f"R={tr['recall']:.4f}  F1={tr['f1']:.4f}  pos_rate={tr['pos_rate']:.4f}  ({tr['time']:.1f}s)")
        te = evaluate(model, test_loader, device)
        print(f"Test   loss={te['loss']:.4f}  acc={te['acc']:.4f}  P={te['precision']:.4f}  "
              f"R={te['recall']:.4f}  F1={te['f1']:.4f}  pos_rate={te['pos_rate']:.4f}  ({te['time']:.1f}s)")

        if te["loss"] < best_test_loss:
            best_test_loss = te["loss"]
            best_epoch = epoch + 1
            save_checkpoint(args.checkpoint, best_epoch, best_test_loss)
            print(f"  -> new best test loss {best_test_loss:.4f} at epoch {best_epoch}; saved {args.checkpoint}")

    print(f"\nTotal training time: {time.time() - total_start:.1f}s")
    print(f"Best test loss {best_test_loss:.4f} at epoch {best_epoch}; checkpoint: {args.checkpoint}")


if __name__ == "__main__":
    main()
