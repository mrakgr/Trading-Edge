#!/usr/bin/env python3
"""
Analyze the distribution of sip_timestamp - participant_timestamp across all
trades in the canonical stocks-in-play set, AFTER applying the same
condition-code filter that the F# pipeline uses.

This matters because the unfiltered distribution is dominated by late-reported
extended-hours trades (conditions 12, 13, 41, etc.) that have hour-scale
deltas by design — participant_ts is the real execution time, sip_ts is when
the consolidated tape caught up. We already drop those in TradeLoader.fs's
`shouldExclude`. What we really want to know is: of the trades that SURVIVE
the filter, are the SIP/participant deltas all under a millisecond?

Output:
  1. Two summary blocks to stdout: filtered and unfiltered distributions.
  2. A bar chart of t-digest centroids (filtered only) saved to
     charts/sip_participant_delta.png.

Runtime: ~10s for the 106-day canonical set.
"""

import glob
import os
import re
import sys
import time
import numpy as np
import duckdb
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from pytdigest import TDigest

TRADES_ROOT = "data/trades"
CANONICAL_PS1 = "docs/generate_stocks_in_play_charts.ps1"
OUT_PNG = "charts/sip_participant_delta.png"

# Conditions we exclude from downstream price-discovery analysis (mirrors
# TradingEdge.Parsing/TradeLoader.fs `excludeConditions`). These are the
# late-reported, out-of-sequence, and special-settlement conditions that
# carry large SIP/participant deltas by design.
EXCLUDE_CONDITIONS = {2, 7, 10, 13, 20, 21, 22, 29, 32, 41, 52, 53}

# Conditions that override the filter — opening/closing prints are always
# kept regardless of other exclude flags.
OPEN_CLOSE_CONDITIONS = {17, 25, 19, 8}


def load_canonical_entries(ps1_path: str) -> set[tuple[str, str]]:
    """Parse `docs/generate_stocks_in_play_charts.ps1` for the $Files array
    of hashtables, returning {(ticker, date), ...}. Mirrors the regex in
    TradingEdge.Optimize/Program.fs."""
    pattern = re.compile(r"""Ticker\s*=\s*['"]([^'"]+)['"][^}]*Date\s*=\s*['"]([^'"]+)['"]""")
    with open(ps1_path) as f:
        text = f.read()
    return set(pattern.findall(text))


def main() -> int:
    canonical = load_canonical_entries(CANONICAL_PS1)
    print(f"Canonical SIP list: {len(canonical)} (ticker, date) pairs")

    all_files = sorted(glob.glob(f"{TRADES_ROOT}/*/*.parquet"))
    # Restrict to the canonical set — we don't care about leftover downloads.
    files = []
    for path in all_files:
        ticker = os.path.basename(os.path.dirname(path))
        date = os.path.splitext(os.path.basename(path))[0]
        if (ticker, date) in canonical:
            files.append(path)
    print(f"Matched {len(files)} parquet files on disk to canonical list")

    conn = duckdb.connect()
    # Expose the exclude set as a DuckDB literal list so we can filter in-engine.
    exclude_list = "[" + ", ".join(str(c) for c in sorted(EXCLUDE_CONDITIONS)) + "]"
    keep_list = "[" + ", ".join(str(c) for c in sorted(OPEN_CLOSE_CONDITIONS)) + "]"

    # Two digests so we can show both the raw (pre-filter) and filtered
    # distributions in the summary. Only the filtered one drives the chart.
    digest_raw = TDigest(compression=500)
    digest_filtered = TDigest(compression=500)

    stats_raw = {"count": 0, "neg": 0, "over_1ms": 0, "over_10ms": 0, "over_100ms": 0, "over_1s": 0,
                 "min": None, "max": None}
    stats_filt = {"count": 0, "neg": 0, "over_1ms": 0, "over_10ms": 0, "over_100ms": 0, "over_1s": 0,
                  "min": None, "max": None}

    per_ticker_max_filt: dict[str, int] = {}
    worst_filt: list[tuple[int, str, str, int]] = []  # (abs, ticker, date, signed)

    def tally(stats: dict, deltas: np.ndarray):
        if deltas.size == 0:
            return
        stats["count"] += int(deltas.size)
        abs_d = np.abs(deltas)
        stats["neg"] += int(np.sum(deltas < 0))
        stats["over_1ms"] += int(np.sum(abs_d > 1_000_000))
        stats["over_10ms"] += int(np.sum(abs_d > 10_000_000))
        stats["over_100ms"] += int(np.sum(abs_d > 100_000_000))
        stats["over_1s"] += int(np.sum(abs_d > 1_000_000_000))
        dmin = int(deltas.min())
        dmax = int(deltas.max())
        stats["min"] = dmin if stats["min"] is None else min(stats["min"], dmin)
        stats["max"] = dmax if stats["max"] is None else max(stats["max"], dmax)

    t_start = time.monotonic()
    for idx, path in enumerate(files, start=1):
        ticker = os.path.basename(os.path.dirname(path))
        date = os.path.splitext(os.path.basename(path))[0]

        # Raw deltas: all non-null rows.
        raw = conn.execute(
            "SELECT CAST(sip_timestamp - participant_timestamp AS BIGINT) AS d "
            f"FROM read_parquet('{path}') "
            "WHERE sip_timestamp IS NOT NULL AND participant_timestamp IS NOT NULL"
        ).fetchnumpy()["d"]

        # Filtered: apply the same shouldExclude logic the F# pipeline uses.
        # The rule is: keep if (any opening/closing-print condition present)
        # OR (no exclude condition present). DuckDB's list_has_any operator
        # (&&) checks for intersection.
        filtered = conn.execute(
            "SELECT CAST(sip_timestamp - participant_timestamp AS BIGINT) AS d "
            f"FROM read_parquet('{path}') "
            "WHERE sip_timestamp IS NOT NULL AND participant_timestamp IS NOT NULL "
            f"  AND (list_has_any(COALESCE(conditions, []::INTEGER[]), {keep_list}::INTEGER[]) "
            f"       OR NOT list_has_any(COALESCE(conditions, []::INTEGER[]), {exclude_list}::INTEGER[]))"
        ).fetchnumpy()["d"]

        digest_raw.update(raw.astype(np.float64))
        digest_filtered.update(filtered.astype(np.float64))
        tally(stats_raw, raw)
        tally(stats_filt, filtered)

        if filtered.size > 0:
            abs_d = np.abs(filtered)
            worst_idx = int(np.argmax(abs_d))
            worst_abs = int(abs_d[worst_idx])
            prev = per_ticker_max_filt.get(ticker, 0)
            if worst_abs > prev:
                per_ticker_max_filt[ticker] = worst_abs
            worst_filt.append((worst_abs, ticker, date, int(filtered[worst_idx])))
            worst_filt.sort(key=lambda x: -x[0])
            worst_filt = worst_filt[:10]

        if idx % 10 == 0 or idx == len(files):
            elapsed = time.monotonic() - t_start
            rate = stats_raw["count"] / elapsed if elapsed > 0 else 0
            kept = 100.0 * stats_filt["count"] / stats_raw["count"] if stats_raw["count"] else 0
            print(f"  [{idx:3d}/{len(files)}] {ticker}/{date}  "
                  f"raw={stats_raw['count']:>12,}  "
                  f"filt={stats_filt['count']:>12,} ({kept:4.1f}%)  "
                  f"elapsed={elapsed:5.1f}s  ({rate/1e6:.1f} M/s)")

    def print_summary(label: str, digest: TDigest, stats: dict):
        n = stats["count"]
        print()
        print("=" * 74)
        print(f"  {label}")
        print("=" * 74)
        print(f"Trades: {n:,}")
        if stats["min"] is not None:
            print(f"Range:  {stats['min']:>20,} ns .. {stats['max']:>20,} ns")
            print(f"        {stats['min']/1e6:>20.3f} ms .. {stats['max']/1e6:>20.3f} ms")

        def pct(k: int) -> str:
            return f"{k:>13,} ({100.0*k/n:>9.6f}%)" if n else "0"

        print()
        print("Over-threshold counts (|delta| > X):")
        print(f"  > 1   ms : {pct(stats['over_1ms'])}")
        print(f"  > 10  ms : {pct(stats['over_10ms'])}")
        print(f"  > 100 ms : {pct(stats['over_100ms'])}")
        print(f"  > 1   s  : {pct(stats['over_1s'])}")
        print(f"Negative deltas (SIP earlier than participant):")
        print(f"             {pct(stats['neg'])}")
        print()
        print("Percentiles (signed delta):")
        for q, lbl in [
            (0.00001, " 0.001%"),
            (0.0001, " 0.01 %"),
            (0.001,  " 0.1  %"),
            (0.01,   " 1    %"),
            (0.10,   "10    %"),
            (0.50,   "50    % (median)"),
            (0.90,   "90    %"),
            (0.99,   "99    %"),
            (0.999,  "99.9  %"),
            (0.9999, "99.99 %"),
            (0.99999, "99.999%"),
        ]:
            v = digest.inverse_cdf(q)
            print(f"  P{lbl:>16}: {v:>18,.0f} ns  ({v/1e6:>12.3f} ms)")

    print_summary("RAW (no filter)", digest_raw, stats_raw)
    print_summary("FILTERED (shouldExclude applied)", digest_filtered, stats_filt)

    print()
    print("Top 10 worst |delta| trades (FILTERED):")
    for abs_d, ticker, date, signed_d in worst_filt:
        print(f"  {ticker:>6}/{date}  signed={signed_d:>18,} ns  ({signed_d/1e6:>12.3f} ms)")

    print()
    print("Top 10 tickers by max |delta| (FILTERED):")
    for t, v in sorted(per_ticker_max_filt.items(), key=lambda x: -x[1])[:10]:
        print(f"  {t:>6}: {v:>18,} ns  ({v/1e6:>12.3f} ms)")

    # -----------------------------------------------------------------
    # Bar chart of centroids. Three panels:
    #   1. Zoomed to ±10ms so the core of the distribution is visible.
    #   2. Full range, symlog, so the long tail is also visible.
    #   3. Raw vs filtered overlay on a symlog x so you can see which
    #      centroids got removed by the filter.
    # -----------------------------------------------------------------
    c_filt = digest_filtered.get_centroids()
    c_raw = digest_raw.get_centroids()
    means_filt = c_filt[:, 0] / 1000.0      # ns -> μs
    weights_filt = c_filt[:, 1]
    means_raw = c_raw[:, 0] / 1000.0
    weights_raw = c_raw[:, 1]

    print()
    print(f"Filtered t-digest has {len(means_filt)} centroids; raw has {len(means_raw)}")

    os.makedirs(os.path.dirname(OUT_PNG), exist_ok=True)
    fig, (ax_zoom, ax_full, ax_compare) = plt.subplots(3, 1, figsize=(14, 14))

    # Panel 1: zoom to ±10ms (±10,000 μs) — the core where 90%+ of trades live.
    mask_zoom = (means_filt >= -10_000) & (means_filt <= 10_000)
    if mask_zoom.sum() > 1:
        m_z = means_filt[mask_zoom]
        w_z = weights_filt[mask_zoom]
        # Use centroid-to-centroid gaps as widths.
        widths = np.diff(m_z, append=m_z[-1] + (m_z[-1] - m_z[-2] if len(m_z) > 1 else 1.0))
        widths = np.maximum(widths, 0.5)
        ax_zoom.bar(m_z, w_z, width=widths, color="steelblue", edgecolor="none", align="edge")
    ax_zoom.set_yscale("log")
    ax_zoom.set_xlabel("sip - participant delta (μs)")
    ax_zoom.set_ylabel("trades (log scale)")
    ax_zoom.set_title(f"FILTERED distribution, zoomed to ±10 ms  "
                      f"(n={stats_filt['count']:,})")
    ax_zoom.axvline(0, color="black", linestyle="--", linewidth=0.8, alpha=0.5)
    ax_zoom.axvline(1000, color="red", linestyle="--", linewidth=0.8, alpha=0.7, label="±1 ms")
    ax_zoom.axvline(-1000, color="red", linestyle="--", linewidth=0.8, alpha=0.7)
    ax_zoom.set_xlim(-10_000, 10_000)
    ax_zoom.legend(loc="upper right")
    ax_zoom.grid(True, alpha=0.3)

    # Panel 2: full range, symlog x axis.
    ax_full.bar(means_filt, weights_filt, width=0.5, color="steelblue", edgecolor="none")
    ax_full.set_xscale("symlog", linthresh=10)
    ax_full.set_yscale("log")
    ax_full.set_xlabel("sip - participant delta (μs, symlog — linear within ±10μs)")
    ax_full.set_ylabel("trades (log scale)")
    ax_full.set_title("FILTERED, full range with symlog x")
    ax_full.axvline(0, color="black", linestyle="--", linewidth=0.8, alpha=0.5)
    ax_full.axvline(1000, color="red", linestyle="--", linewidth=0.8, alpha=0.7, label="±1 ms")
    ax_full.axvline(-1000, color="red", linestyle="--", linewidth=0.8, alpha=0.7)
    ax_full.legend(loc="upper right")
    ax_full.grid(True, alpha=0.3)

    # Panel 3: raw vs filtered overlay.
    ax_compare.bar(means_raw, weights_raw, width=0.5, color="lightgray",
                   edgecolor="none", label="raw (no filter)")
    ax_compare.bar(means_filt, weights_filt, width=0.5, color="steelblue",
                   edgecolor="none", alpha=0.7, label="filtered (shouldExclude)")
    ax_compare.set_xscale("symlog", linthresh=10)
    ax_compare.set_yscale("log")
    ax_compare.set_xlabel("sip - participant delta (μs, symlog)")
    ax_compare.set_ylabel("trades (log scale)")
    ax_compare.set_title("Raw vs filtered — what the exclude filter actually removes")
    ax_compare.axvline(0, color="black", linestyle="--", linewidth=0.8, alpha=0.5)
    ax_compare.legend(loc="upper right")
    ax_compare.grid(True, alpha=0.3)

    plt.tight_layout()
    plt.savefig(OUT_PNG, dpi=120)
    print(f"Saved {OUT_PNG}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
