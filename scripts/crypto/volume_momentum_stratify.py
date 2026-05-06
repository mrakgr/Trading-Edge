"""Stratify a trips CSV by trailing-volume momentum at entry.

For each trip:
  1. Load the symbol's 1m bar parquet.
  2. Compute the recent volume: sum of bar volume over the trailing
     `--recent-hours` ending at entry_us.
  3. Compute the baseline volume: mean per-`recent-hours` window's
     summed volume over the trailing `--lookback-days`. We compute
     this as `total_volume_lookback / (lookback_days * 24 / recent_hours)`.
  4. ratio = recent_volume / baseline_volume — multiplier of typical
     activity over the recent window.
  5. Bucket trades by ratio cutpoints, breakdown per side.

Reference quantity is the cumulative bar volume — no VWMA option since
volume isn't a price (no "volume-weighted volume"). For symbols with
< lookback-days of bars at entry, we use whatever's available (still
produces a meaningful ratio over the shorter baseline).

Per-symbol streaming: only one symbol's bar parquet is in memory at a time.

Default trips file is the z-persist no-stop run.

Use:

    python scripts/crypto/volume_momentum_stratify.py \\
        --trips data/crypto/cumsum_z_persistexit/backtest_results_trips_1m_th15_ls.csv
"""

import argparse
import os
import sys

import duckdb
import pandas as pd


DEFAULT_TRIPS = "data/crypto/cumsum_z_persistexit/backtest_results_trips_1m_th15_ls.csv"
DEFAULT_BARS_ROOT = "data/crypto/perps_bars/1m"
DEFAULT_LOOKBACK_DAYS = 60
DEFAULT_RECENT_HOURS = 24
US_PER_DAY = 86_400_000_000
US_PER_HOUR = 3_600_000_000

# Volume-ratio cutpoints. Log-spaced. Buckets:
#   <0.5x  | 0.5-1x | 1-1.5x | 1.5-2x | 2-3x | 3-5x | 5-10x | >=10x
CUTPOINTS = [0.5, 1.0, 1.5, 2.0, 3.0, 5.0, 10.0]
BUCKET_LABELS = [
    "<0.5x",
    "0.5 to 1x",
    "1 to 1.5x",
    "1.5 to 2x",
    "2 to 3x",
    "3 to 5x",
    "5 to 10x",
    ">=10x",
]


def bucket_idx(p: float, cutpoints) -> int:
    for i, c in enumerate(cutpoints):
        if p < c:
            return i
    return len(cutpoints)


def per_symbol_volume_ratio(con, bars_path: str, trips_for_sym: pd.DataFrame,
                            recent_us: int, lookback_us: int) -> pd.DataFrame:
    """Compute, for every trip on this symbol:
        recent_vol   = sum(volume) over (entry_us - recent_us, entry_us]
        baseline_vol = avg-per-recent-window volume over the trailing
                       `lookback_us` ending at (entry_us - recent_us)
        ratio        = recent_vol / baseline_vol

    The baseline excludes the recent window itself (so an active recent
    period doesn't inflate the denominator and dilute the signal).

    Implemented via three ASOF joins against running cum_v: at entry,
    at (entry - recent), and at (entry - recent - lookback). Two
    differences give the recent and baseline cumulative volumes; the
    baseline is then scaled to per-recent-window units.
    """
    con.execute(f"""
        CREATE OR REPLACE TEMP TABLE bars AS
        SELECT
            start_us,
            SUM(volume)  OVER (ORDER BY start_us)   AS cum_v
        FROM read_parquet('{bars_path}')
        WHERE volume > 0
        ORDER BY start_us;
    """)
    con.register("trips_sym", trips_for_sym[["entry_us"]])
    con.execute("""
        CREATE OR REPLACE TEMP TABLE trip_targets AS
        SELECT
            entry_us,
            entry_us                                               AS at_entry_us,
            CAST(entry_us AS BIGINT) - CAST(? AS BIGINT)           AS at_recent_us,
            CAST(entry_us AS BIGINT) - CAST(? AS BIGINT) - CAST(? AS BIGINT)
                                                                   AS at_baseline_us
        FROM trips_sym;
    """, [recent_us, recent_us, lookback_us])

    df = con.execute("""
        WITH at_entry AS (
            SELECT t.entry_us, b.cum_v AS cum_v_now,
                   b.start_us AS bar_us_now
            FROM trip_targets t
            ASOF LEFT JOIN bars b ON t.at_entry_us > b.start_us
        ),
        at_recent AS (
            SELECT t.entry_us, b.cum_v AS cum_v_recent,
                   b.start_us AS bar_us_recent
            FROM trip_targets t
            ASOF LEFT JOIN bars b ON t.at_recent_us > b.start_us
        ),
        at_baseline AS (
            SELECT t.entry_us, b.cum_v AS cum_v_baseline,
                   b.start_us AS bar_us_baseline
            FROM trip_targets t
            ASOF LEFT JOIN bars b ON t.at_baseline_us > b.start_us
        )
        SELECT
            ae.entry_us,
            ae.cum_v_now,
            ae.bar_us_now,
            COALESCE(ar.cum_v_recent, 0.0)   AS cum_v_recent,
            ar.bar_us_recent,
            COALESCE(ab.cum_v_baseline, 0.0) AS cum_v_baseline,
            ab.bar_us_baseline
        FROM at_entry ae
        LEFT JOIN at_recent   ar USING (entry_us)
        LEFT JOIN at_baseline ab USING (entry_us)
    """).fetchdf()

    # Recent-window volume = cum at entry minus cum at entry - recent_us.
    recent_vol = df["cum_v_now"] - df["cum_v_recent"]
    # Baseline-window volume (raw, in lookback units) = cum at recent
    # minus cum at recent - lookback_us. When the at_baseline ASOF found
    # nothing (symbol younger than lookback at the time we needed it),
    # cum_v_baseline = 0 and we get the cumulative from the symbol's start.
    baseline_total = df["cum_v_recent"] - df["cum_v_baseline"]
    # Per-recent-window units: scale by (lookback / recent).
    recent_windows_in_lookback = lookback_us / recent_us
    baseline_per_recent = baseline_total / recent_windows_in_lookback

    df["ratio"] = recent_vol / baseline_per_recent
    df.loc[(baseline_per_recent <= 0) | df["cum_v_now"].isna(), "ratio"] = pd.NA

    # Lookback days actually used: from the earliest baseline anchor to entry.
    earliest_us = con.execute("SELECT min(start_us) FROM bars").fetchone()[0]
    baseline_start = df["bar_us_baseline"].fillna(earliest_us)
    df["lookback_days"] = (df["entry_us"] - baseline_start) / US_PER_DAY

    out = trips_for_sym.merge(df[["entry_us", "ratio", "lookback_days"]],
                              on="entry_us", how="left")
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--trips", default=DEFAULT_TRIPS,
                    help=f"Trips CSV. Default: {DEFAULT_TRIPS}")
    ap.add_argument("--bars-root", default=DEFAULT_BARS_ROOT,
                    help=f"1m bar-parquet root. Default: {DEFAULT_BARS_ROOT}")
    ap.add_argument("--lookback-days", type=int, default=DEFAULT_LOOKBACK_DAYS,
                    help=f"Baseline-volume window in days. Default: {DEFAULT_LOOKBACK_DAYS}")
    ap.add_argument("--recent-hours", type=int, default=DEFAULT_RECENT_HOURS,
                    help=f"Recent-volume window in hours. Default: {DEFAULT_RECENT_HOURS}")
    args = ap.parse_args()

    repo_root = os.path.abspath(os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "..", ".."))
    trips_path = os.path.join(repo_root, args.trips)
    bars_root = os.path.join(repo_root, args.bars_root)
    lookback_us = args.lookback_days * US_PER_DAY
    recent_us = args.recent_hours * US_PER_HOUR

    print(f"Recent-volume window: {args.recent_hours}h, baseline lookback: {args.lookback_days}d")
    trips = pd.read_csv(trips_path)
    print(f"Loaded {len(trips):,} trips from {args.trips}")

    by_symbol = dict(tuple(trips.groupby("symbol", sort=False)))
    print(f"  across {len(by_symbol):,} symbols")
    print()

    con = duckdb.connect()

    pieces = []
    n_missing = 0
    for i, (sym, sub) in enumerate(by_symbol.items(), start=1):
        bars_path = os.path.join(bars_root, f"{sym}.parquet")
        if not os.path.exists(bars_path):
            n_missing += 1
            continue
        result = per_symbol_volume_ratio(con, bars_path, sub, recent_us, lookback_us)
        pieces.append(result)
        if i % 50 == 0 or i == len(by_symbol):
            print(f"  [{i:>4d}/{len(by_symbol)}] processed", file=sys.stderr)

    if n_missing > 0:
        print(f"  ({n_missing} symbols had no parquet, skipped)")

    df = pd.concat(pieces, ignore_index=True)
    df = df.dropna(subset=["ratio"])
    print(f"Computed volume ratio for {len(df):,} trips")
    print()

    # Sanity check: lookback days actually available.
    print("Lookback-days distribution (across all trips):")
    pct = df["lookback_days"].quantile([0.05, 0.25, 0.50, 0.75, 0.95])
    print(f"  p5={pct[0.05]:.1f}  p25={pct[0.25]:.1f}  med={pct[0.50]:.1f}  "
          f"p75={pct[0.75]:.1f}  p95={pct[0.95]:.1f}")
    n_short = int((df["lookback_days"] < args.lookback_days).sum())
    print(f"  {n_short:,} trips ({100.0*n_short/len(df):.1f}%) had <{args.lookback_days}d available")
    print()

    df["bucket"] = df["ratio"].apply(lambda p: bucket_idx(p, CUTPOINTS))

    for side_label, side_str in [("LONG", "long"), ("SHORT", "short")]:
        sub = df[df["side"] == side_str]
        if len(sub) == 0:
            continue
        print(f"=== {side_label} ({len(sub):,} trades) ===")
        print(f"  {'bucket':<12s}  {'trades':>8s}  {'win%':>6s}  "
              f"{'PF':>6s}  {'net_pnl$':>11s}  {'avg_pnl$':>9s}")
        print("  " + "-" * 64)
        for i, label in enumerate(BUCKET_LABELS):
            b = sub[sub["bucket"] == i]
            if len(b) == 0:
                continue
            wins = (b["net_pnl"] > 0).sum()
            losses_sum = -b.loc[b["net_pnl"] < 0, "net_pnl"].sum()
            wins_sum = b.loc[b["net_pnl"] > 0, "net_pnl"].sum()
            pf = (wins_sum / losses_sum) if losses_sum > 0 \
                 else float("inf") if wins_sum > 0 else 0.0
            avg_pnl = b["net_pnl"].mean()
            net_pnl = b["net_pnl"].sum()
            wr = 100.0 * wins / len(b)
            pf_s = f"{pf:>6.2f}" if pf != float("inf") else "   inf"
            print(f"  {label:<12s}  {len(b):>8d}  {wr:>5.1f}%  "
                  f"{pf_s}  {net_pnl:>+11,.0f}  {avg_pnl:>+9,.2f}")
        total_pnl = sub["net_pnl"].sum()
        total_wins = (sub["net_pnl"] > 0).sum()
        wins_sum = sub.loc[sub["net_pnl"] > 0, "net_pnl"].sum()
        losses_sum = -sub.loc[sub["net_pnl"] < 0, "net_pnl"].sum()
        pf = (wins_sum / losses_sum) if losses_sum > 0 else 0.0
        wr = 100.0 * total_wins / len(sub)
        print("  " + "-" * 64)
        print(f"  {'TOTAL':<12s}  {len(sub):>8d}  {wr:>5.1f}%  "
              f"{pf:>6.2f}  {total_pnl:>+11,.0f}  "
              f"{sub['net_pnl'].mean():>+9,.2f}")
        print()


if __name__ == "__main__":
    main()
