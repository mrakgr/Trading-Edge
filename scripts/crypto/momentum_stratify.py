"""Stratify a trips CSV by 60-day trailing VWMA momentum at entry.

For each trip:
  1. Load the symbol's 1m bar parquet.
  2. Compute the 60-day trailing volume-weighted moving average of VWAP
     ending at entry_us:
        vwma_60d = sum(vwap * volume) / sum(volume)  over (entry_us - 60d, entry_us]
     If the symbol has < 60 days of bars available, use whatever bars
     exist before entry_us — VWMA is still well-defined on a short window.
  3. Compute pct_change = entry_price / vwma_60d - 1.
  4. Bucket trades by pct_change cutpoints, breakdown per side.

Per-symbol streaming: only one symbol's bar parquet is in memory at a time,
so memory stays bounded regardless of universe size.

Default trips file is the z-persist no-stop run.

Use:

    python scripts/crypto/momentum_stratify.py \\
        --trips data/crypto/cumsum_z_persistexit/backtest_results_trips_1m_th15_ls.csv
"""

import argparse
import os
import sys

import duckdb
import pandas as pd


DEFAULT_TRIPS = "data/crypto/cumsum_z_persistexit/backtest_results_trips_1m_th15_ls.csv"
DEFAULT_BARS_ROOT = "data/crypto/perps_bars/1m"
LOOKBACK_DAYS = 60
US_PER_DAY = 86_400_000_000

CUTPOINTS = [-0.50, -0.20, -0.05, +0.05, +0.20, +0.50, +1.00, +2.00]
BUCKET_LABELS = [
    "<-50%",
    "-50 to -20%",
    "-20 to -5%",
    "-5 to +5%",
    "+5 to +20%",
    "+20 to +50%",
    "+50 to +100%",
    "+100 to +200%",
    ">=+200%",
]

# Z-score cutpoints (used when --zscore). Roughly equal-density slices
# under a normal distribution; gives 8 buckets.
Z_CUTPOINTS = [-3.0, -2.0, -1.0, 0.0, +1.0, +2.0, +3.0]
Z_BUCKET_LABELS = [
    "<-3",
    "-3 to -2",
    "-2 to -1",
    "-1 to 0",
    "0 to +1",
    "+1 to +2",
    "+2 to +3",
    ">=+3",
]


def bucket_idx(p: float, cutpoints) -> int:
    for i, c in enumerate(cutpoints):
        if p < c:
            return i
    return len(cutpoints)


def per_symbol_pct_change(con, bars_path: str, trips_for_sym: pd.DataFrame,
                          lookback_us: int, mode: str) -> pd.DataFrame:
    """Return a DataFrame with the original trip rows plus pct_change/zscore
    and lookback_days columns, computed against the symbol's 1m bar parquet.

    Modes:
      vwma:    ref = sum(vwap*volume) / sum(volume)  over the trailing 60d
               metric = entry_price / ref - 1                            (% change)
      ma:      ref = avg(vwap)                       over the trailing 60d
               metric = entry_price / ref - 1                            (% change)
      zscore:  ref = avg(vwap), std = stddev_samp(vwap)  over the trailing 60d
               metric = (entry_price - ref) / ref / pct_std              (z-score
               in units of the per-bar log-return-equivalent std).
               Actually we use price-units std: z = (entry - mean) / std.

    Cumulative sums let us evaluate the windowed aggregate as
        (cum_at_t - cum_at_(t-60d)) / (cum_count_at_t - cum_count_at_(t-60d))
    via two ASOF joins (one at the entry timestamp, one 60 days earlier).
    If the second ASOF returns nothing (symbol younger than 60d at entry),
    its sums default to 0 — the formula then reads off the symbol's full
    available history before the entry, which matches the spec.

    For z-score mode we also keep cum_sumsq (running sum of vwap²) so we
    can compute the windowed sample-variance via Welford's algebraic
    identity: var = (sumsq_diff - n * mean²) / (n - 1).
    """
    # Load and pre-aggregate the symbol's bars in DuckDB.
    if mode == "vwma":
        con.execute(f"""
            CREATE OR REPLACE TEMP TABLE bars AS
            SELECT
                start_us,
                SUM(vwap * volume) OVER (ORDER BY start_us)   AS cum_num,
                SUM(volume)        OVER (ORDER BY start_us)   AS cum_den,
                CAST(NULL AS DOUBLE)                          AS cum_sumsq
            FROM read_parquet('{bars_path}')
            WHERE volume > 0 AND vwap > 0
            ORDER BY start_us;
        """)
    else:
        # Unweighted MA / z-score: cum_num is sum of vwap, cum_den is bar count.
        # Always compute cum_sumsq (sum of vwap²) so the same path serves both modes.
        con.execute(f"""
            CREATE OR REPLACE TEMP TABLE bars AS
            SELECT
                start_us,
                SUM(vwap)          OVER (ORDER BY start_us)   AS cum_num,
                ROW_NUMBER()       OVER (ORDER BY start_us)   AS cum_den,
                SUM(vwap * vwap)   OVER (ORDER BY start_us)   AS cum_sumsq
            FROM read_parquet('{bars_path}')
            WHERE volume > 0 AND vwap > 0
            ORDER BY start_us;
        """)
    # Trip entry timestamps for this symbol.
    con.register("trips_sym", trips_for_sym[["entry_us"]])
    con.execute("""
        CREATE OR REPLACE TEMP TABLE trip_targets AS
        SELECT
            entry_us,
            entry_us            AS at_entry_us,
            CAST(entry_us AS BIGINT) - CAST(? AS BIGINT) AS at_lookback_us
        FROM trips_sym;
    """, [lookback_us])

    # ASOF LEFT JOIN: pull cum_num / cum_den / cum_sumsq at-or-before each timestamp.
    df = con.execute("""
        WITH at_entry AS (
            SELECT t.entry_us,
                   b.cum_num AS cum_num_now,
                   b.cum_den AS cum_den_now,
                   b.cum_sumsq AS cum_sumsq_now,
                   b.start_us AS bar_us_now
            FROM trip_targets t
            ASOF LEFT JOIN bars b
              ON t.at_entry_us > b.start_us
        ),
        at_lookback AS (
            SELECT t.entry_us,
                   b.cum_num AS cum_num_lb,
                   b.cum_den AS cum_den_lb,
                   b.cum_sumsq AS cum_sumsq_lb,
                   b.start_us AS bar_us_lb
            FROM trip_targets t
            ASOF LEFT JOIN bars b
              ON t.at_lookback_us > b.start_us
        )
        SELECT
            ae.entry_us,
            ae.cum_num_now,
            ae.cum_den_now,
            ae.cum_sumsq_now,
            ae.bar_us_now,
            COALESCE(al.cum_num_lb, 0.0) AS cum_num_lb,
            COALESCE(al.cum_den_lb, 0.0) AS cum_den_lb,
            COALESCE(al.cum_sumsq_lb, 0.0) AS cum_sumsq_lb,
            al.bar_us_lb
        FROM at_entry ae
        LEFT JOIN at_lookback al USING (entry_us)
    """).fetchdf()

    # Windowed reference = (cum_num_now - cum_num_lb) / (cum_den_now - cum_den_lb).
    # When the lookback ASOF found nothing (symbol younger than 60d), the lb
    # values are 0 and we get the cumulative aggregate over all available history.
    num_diff = df["cum_num_now"] - df["cum_num_lb"]
    den_diff = df["cum_den_now"] - df["cum_den_lb"]
    df["ref_price"] = num_diff / den_diff
    df.loc[(den_diff <= 0) | df["cum_num_now"].isna(), "ref_price"] = pd.NA

    if mode == "zscore":
        # Windowed sample variance via Welford identity:
        #   var = (sumsq_diff - n * mean²) / (n - 1)
        sumsq_diff = df["cum_sumsq_now"] - df["cum_sumsq_lb"]
        n = den_diff
        mean = df["ref_price"]
        # Avoid division by zero for symbols with only a single bar in window.
        var = (sumsq_diff - n * mean * mean) / (n - 1)
        var = var.clip(lower=0.0)  # numeric safety
        std = var ** 0.5
        df["ref_std"] = std
        df.loc[(n <= 1) | std.isna() | (std <= 0), "ref_std"] = pd.NA

    # Lookback days actually used: from the first bar in the window to entry.
    # If the lookback ASOF returned a bar, the window starts there; otherwise
    # the window starts at the symbol's first bar.
    earliest_us = con.execute(
        "SELECT min(start_us) FROM bars"
    ).fetchone()[0]
    window_start = df["bar_us_lb"].fillna(earliest_us)
    df["lookback_days"] = (df["entry_us"] - window_start) / US_PER_DAY

    # Merge back onto the original trips_for_sym dataframe.
    if mode == "zscore":
        out = trips_for_sym.merge(
            df[["entry_us", "ref_price", "ref_std", "lookback_days"]],
            on="entry_us", how="left")
        out["pct_change"] = (out["entry_price"] - out["ref_price"]) / out["ref_std"]
    else:
        out = trips_for_sym.merge(
            df[["entry_us", "ref_price", "lookback_days"]],
            on="entry_us", how="left")
        out["pct_change"] = (out["entry_price"] / out["ref_price"]) - 1.0
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--trips", default=DEFAULT_TRIPS,
                    help=f"Trips CSV. Default: {DEFAULT_TRIPS}")
    ap.add_argument("--bars-root", default=DEFAULT_BARS_ROOT,
                    help=f"1m bar-parquet root. Default: {DEFAULT_BARS_ROOT}")
    ap.add_argument("--lookback-days", type=int, default=LOOKBACK_DAYS,
                    help=f"Reference-price window in days. Default: {LOOKBACK_DAYS}")
    ap.add_argument("--mode", choices=["vwma", "ma", "zscore"], default="vwma",
                    help="Reference-price computation mode. "
                         "vwma = volume-weighted moving average (default). "
                         "ma = unweighted mean of bar VWAP. "
                         "zscore = (entry - unweighted_mean) / unweighted_std, "
                         "in price-units σ.")
    ap.add_argument("--unweighted", action="store_true",
                    help="Shortcut for --mode ma (kept for backwards compat).")
    args = ap.parse_args()
    if args.unweighted and args.mode == "vwma":
        args.mode = "ma"

    repo_root = os.path.abspath(os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "..", ".."))
    trips_path = os.path.join(repo_root, args.trips)
    bars_root = os.path.join(repo_root, args.bars_root)
    lookback_us = args.lookback_days * US_PER_DAY

    mode_desc = {
        "vwma": "VWMA (volume-weighted)",
        "ma": "MA (unweighted mean of VWAP)",
        "zscore": "Z-score (entry - unweighted_mean) / unweighted_std",
    }[args.mode]
    print(f"Reference-price mode: {mode_desc}, lookback {args.lookback_days}d")
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
        result = per_symbol_pct_change(con, bars_path, sub, lookback_us,
                                       mode=args.mode)
        pieces.append(result)
        if i % 50 == 0 or i == len(by_symbol):
            print(f"  [{i:>4d}/{len(by_symbol)}] processed", file=sys.stderr)

    if n_missing > 0:
        print(f"  ({n_missing} symbols had no parquet, skipped)")

    df = pd.concat(pieces, ignore_index=True)
    df = df.dropna(subset=["pct_change"])
    print(f"Computed pct_change for {len(df):,} trips")
    print()

    # Lookback distribution sanity check.
    print("Lookback-days distribution (across all trips):")
    pct = df["lookback_days"].quantile([0.05, 0.25, 0.50, 0.75, 0.95])
    print(f"  p5={pct[0.05]:.1f}  p25={pct[0.25]:.1f}  med={pct[0.50]:.1f}  "
          f"p75={pct[0.75]:.1f}  p95={pct[0.95]:.1f}")
    n_short = int((df["lookback_days"] < args.lookback_days).sum())
    print(f"  {n_short:,} trips ({100.0*n_short/len(df):.1f}%) had <{args.lookback_days}d available")
    print()

    cutpoints = Z_CUTPOINTS if args.mode == "zscore" else CUTPOINTS
    bucket_labels = Z_BUCKET_LABELS if args.mode == "zscore" else BUCKET_LABELS
    df["bucket"] = df["pct_change"].apply(lambda p: bucket_idx(p, cutpoints))

    for side_label, side_str in [("LONG", "long"), ("SHORT", "short")]:
        sub = df[df["side"] == side_str]
        if len(sub) == 0:
            continue
        print(f"=== {side_label} ({len(sub):,} trades) ===")
        print(f"  {'bucket':<16s}  {'trades':>8s}  {'win%':>6s}  "
              f"{'PF':>6s}  {'net_pnl$':>11s}  {'avg_pnl$':>9s}  "
              f"{'med_lookback':>14s}")
        print("  " + "-" * 80)
        for i, label in enumerate(bucket_labels):
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
            med_lb = b["lookback_days"].median()
            wr = 100.0 * wins / len(b)
            pf_s = f"{pf:>6.2f}" if pf != float("inf") else "   inf"
            print(f"  {label:<16s}  {len(b):>8d}  {wr:>5.1f}%  "
                  f"{pf_s}  {net_pnl:>+11,.0f}  {avg_pnl:>+9,.2f}  "
                  f"{med_lb:>13.1f}d")
        total_pnl = sub["net_pnl"].sum()
        total_wins = (sub["net_pnl"] > 0).sum()
        wins_sum = sub.loc[sub["net_pnl"] > 0, "net_pnl"].sum()
        losses_sum = -sub.loc[sub["net_pnl"] < 0, "net_pnl"].sum()
        pf = (wins_sum / losses_sum) if losses_sum > 0 else 0.0
        wr = 100.0 * total_wins / len(sub)
        print("  " + "-" * 80)
        print(f"  {'TOTAL':<16s}  {len(sub):>8d}  {wr:>5.1f}%  "
              f"{pf:>6.2f}  {total_pnl:>+11,.0f}  "
              f"{sub['net_pnl'].mean():>+9,.2f}")
        print()


if __name__ == "__main__":
    main()
