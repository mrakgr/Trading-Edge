"""Stratify a trips CSV by entry hour-of-day (UTC) and day-of-week.

For each trip, derive:
    hour_utc = (entry_us / 3600e6) mod 24
    dow      = day-of-week from entry_us (Mon=0..Sun=6)

Reports per-side breakdown by hour-of-day and day-of-week.

Default trips file is the z-persist no-stop run.

Use:

    python scripts/crypto/time_of_day_stratify.py
"""

import argparse
import os

import duckdb
import pandas as pd


DEFAULT_TRIPS = "data/crypto/cumsum_z_persistexit/backtest_results_trips_1m_th15_ls.csv"

DOW_LABELS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]


def print_side_table(df: pd.DataFrame, side_str: str, group_col: str,
                     group_labels):
    sub = df[df["side"] == side_str].copy()
    if len(sub) == 0:
        return
    side_print = "LONG" if side_str == "long" else "SHORT"
    print(f"--- {side_print} ({len(sub):,} trades) ---")
    print(f"  {group_col:<10s}  {'trades':>7s}  {'win%':>6s}  "
          f"{'PF':>6s}  {'net_pnl$':>11s}  {'avg_pnl$':>9s}")
    print("  " + "-" * 64)
    grouped = sub.groupby(group_col)
    for key in group_labels:
        if key not in grouped.groups:
            continue
        b = grouped.get_group(key)
        wins = (b["net_pnl"] > 0).sum()
        losses_sum = -b.loc[b["net_pnl"] < 0, "net_pnl"].sum()
        wins_sum = b.loc[b["net_pnl"] > 0, "net_pnl"].sum()
        pf = (wins_sum / losses_sum) if losses_sum > 0 \
             else float("inf") if wins_sum > 0 else 0.0
        avg_pnl = b["net_pnl"].mean()
        net_pnl = b["net_pnl"].sum()
        wr = 100.0 * wins / len(b)
        pf_s = f"{pf:>6.2f}" if pf != float("inf") else "   inf"
        if isinstance(key, int) and group_col == "hour_utc":
            label = f"{key:02d}:00"
        else:
            label = str(key)
        print(f"  {label:<10s}  {len(b):>7d}  {wr:>5.1f}%  "
              f"{pf_s}  {net_pnl:>+11,.0f}  {avg_pnl:>+9,.2f}")
    total_pnl = sub["net_pnl"].sum()
    total_wins = (sub["net_pnl"] > 0).sum()
    wins_sum = sub.loc[sub["net_pnl"] > 0, "net_pnl"].sum()
    losses_sum = -sub.loc[sub["net_pnl"] < 0, "net_pnl"].sum()
    pf = (wins_sum / losses_sum) if losses_sum > 0 else 0.0
    wr = 100.0 * total_wins / len(sub)
    print("  " + "-" * 64)
    print(f"  {'TOTAL':<10s}  {len(sub):>7d}  {wr:>5.1f}%  "
          f"{pf:>6.2f}  {total_pnl:>+11,.0f}  {sub['net_pnl'].mean():>+9,.2f}")


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--trips", default=DEFAULT_TRIPS)
    args = ap.parse_args()

    repo_root = os.path.abspath(os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "..", ".."))
    trips_path = os.path.join(repo_root, args.trips)

    con = duckdb.connect()
    df = con.execute(f"""
        SELECT
            entry_us, side, net_pnl,
            CAST(strftime(make_timestamp(entry_us), '%H') AS INTEGER) AS hour_utc,
            CAST(strftime(make_timestamp(entry_us), '%w') AS INTEGER) AS dow_sun0
        FROM read_csv_auto('{trips_path}')
    """).fetchdf()
    # DuckDB %w: Sun=0..Sat=6. Remap to Mon=0..Sun=6.
    df["dow"] = ((df["dow_sun0"] - 1) % 7).map(lambda i: DOW_LABELS[i])

    print(f"Loaded {len(df):,} trips from {args.trips}")
    print()

    # === Hour of day ===
    print("=" * 64)
    print("HOUR OF DAY (UTC)")
    print("=" * 64)
    hours = list(range(24))
    print()
    print_side_table(df, "long", "hour_utc", hours)
    print()
    print_side_table(df, "short", "hour_utc", hours)
    print()

    # === Day of week ===
    print("=" * 64)
    print("DAY OF WEEK (UTC)")
    print("=" * 64)
    print()
    print_side_table(df, "long", "dow", DOW_LABELS)
    print()
    print_side_table(df, "short", "dow", DOW_LABELS)
    print()


if __name__ == "__main__":
    main()
