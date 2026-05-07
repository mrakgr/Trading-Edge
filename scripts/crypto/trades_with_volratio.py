"""Emit a per-trade CSV with the volume-momentum ratio attached.

This is a sibling of `volume_momentum_stratify.py`: same per-symbol
DuckDB streaming logic, but instead of printing bucket aggregates we
write the augmented trips back out as CSV. That way we can drill into
individual trades inside specific buckets (e.g. the 5-10x and >=10x
shorts).

Default params match the table the user liked: 30d baseline / 8h recent.

Use:

    python scripts/crypto/trades_with_volratio.py \\
        --trips data/crypto/cumsum_z_persistexit/backtest_results_trips_1m_th15_ls.csv \\
        --out   data/crypto/cumsum_z_persistexit/trips_th15_volratio_30d8h.csv
"""

import argparse
import os
import sys

import duckdb
import pandas as pd


DEFAULT_TRIPS = "data/crypto/cumsum_z_persistexit/backtest_results_trips_1m_th15_ls.csv"
DEFAULT_BARS_ROOT = "data/crypto/perps_bars/1m"
DEFAULT_LOOKBACK_DAYS = 30
DEFAULT_RECENT_HOURS = 8
US_PER_DAY = 86_400_000_000
US_PER_HOUR = 3_600_000_000


def per_symbol_volume_ratio(con, bars_path: str, trips_for_sym: pd.DataFrame,
                            recent_us: int, lookback_us: int) -> pd.DataFrame:
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
            COALESCE(ar.cum_v_recent,   0.0) AS cum_v_recent,
            COALESCE(ab.cum_v_baseline, 0.0) AS cum_v_baseline,
            ab.bar_us_baseline
        FROM at_entry ae
        LEFT JOIN at_recent   ar USING (entry_us)
        LEFT JOIN at_baseline ab USING (entry_us)
    """).fetchdf()

    recent_vol = df["cum_v_now"] - df["cum_v_recent"]
    baseline_total = df["cum_v_recent"] - df["cum_v_baseline"]
    recent_windows_in_lookback = lookback_us / recent_us
    baseline_per_recent = baseline_total / recent_windows_in_lookback

    df["ratio"] = recent_vol / baseline_per_recent
    df.loc[(baseline_per_recent <= 0) | df["cum_v_now"].isna(), "ratio"] = pd.NA

    earliest_us = con.execute("SELECT min(start_us) FROM bars").fetchone()[0]
    baseline_start = df["bar_us_baseline"].fillna(earliest_us)
    df["lookback_days"] = (df["entry_us"] - baseline_start) / US_PER_DAY
    df["recent_vol"] = recent_vol
    df["baseline_vol_per_recent"] = baseline_per_recent

    out = trips_for_sym.merge(
        df[["entry_us", "ratio", "lookback_days", "recent_vol", "baseline_vol_per_recent"]],
        on="entry_us", how="left")
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--trips", default=DEFAULT_TRIPS)
    ap.add_argument("--bars-root", default=DEFAULT_BARS_ROOT)
    ap.add_argument("--lookback-days", type=int, default=DEFAULT_LOOKBACK_DAYS)
    ap.add_argument("--recent-hours", type=int, default=DEFAULT_RECENT_HOURS)
    ap.add_argument("--out", required=True, help="Output CSV path.")
    args = ap.parse_args()

    repo_root = os.path.abspath(os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "..", ".."))
    trips_path = os.path.join(repo_root, args.trips) if not os.path.isabs(args.trips) else args.trips
    bars_root = os.path.join(repo_root, args.bars_root) if not os.path.isabs(args.bars_root) else args.bars_root
    out_path = os.path.join(repo_root, args.out) if not os.path.isabs(args.out) else args.out

    lookback_us = args.lookback_days * US_PER_DAY
    recent_us = args.recent_hours * US_PER_HOUR

    print(f"Recent window {args.recent_hours}h, baseline {args.lookback_days}d")
    trips = pd.read_csv(trips_path)
    print(f"Loaded {len(trips):,} trips")

    by_symbol = dict(tuple(trips.groupby("symbol", sort=False)))
    con = duckdb.connect()

    pieces = []
    n_missing = 0
    for i, (sym, sub) in enumerate(by_symbol.items(), start=1):
        bars_path = os.path.join(bars_root, f"{sym}.parquet")
        if not os.path.exists(bars_path):
            n_missing += 1
            continue
        pieces.append(per_symbol_volume_ratio(con, bars_path, sub, recent_us, lookback_us))
        if i % 100 == 0:
            print(f"  [{i}/{len(by_symbol)}]", file=sys.stderr)

    if n_missing:
        print(f"  ({n_missing} symbols had no parquet, skipped)")

    df = pd.concat(pieces, ignore_index=True)
    df = df.dropna(subset=["ratio"])
    df.to_csv(out_path, index=False)
    print(f"Wrote {len(df):,} trips with ratio to {out_path}")


if __name__ == "__main__":
    main()
