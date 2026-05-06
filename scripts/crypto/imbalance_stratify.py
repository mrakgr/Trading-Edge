"""Stratify a trips CSV by buy/sell dollar-volume imbalance at entry.

For each trip and each window length, compute:
    imbalance = (Σ buy_dv - Σ sell_dv) / (Σ buy_dv + Σ sell_dv)
over the trailing window ending at entry_us. Range: [-1, +1].

Per-symbol streaming via cumulative sums of buy_dv and sell_dv. Two ASOF
joins (entry, entry - lookback) give the windowed sums in O(1) per trip.

Default trips file is the z-persist no-stop run. Default windows cover
the spec list: 30d, 200h, 24h, 16h, 8h.

Use:

    python scripts/crypto/imbalance_stratify.py
    python scripts/crypto/imbalance_stratify.py --windows 30d 24h 8h
"""

import argparse
import os
import re
import sys

import duckdb
import pandas as pd


DEFAULT_TRIPS = "data/crypto/cumsum_z_persistexit/backtest_results_trips_1m_th15_ls.csv"
DEFAULT_BARS_ROOT = "data/crypto/perps_bars/1m"
US_PER_DAY = 86_400_000_000
US_PER_HOUR = 3_600_000_000
US_PER_MIN = 60_000_000

CUTPOINTS = [-0.5, -0.2, -0.05, +0.05, +0.2, +0.5]
BUCKET_LABELS = [
    "<-50%",
    "-50 to -20%",
    "-20 to -5%",
    "-5 to +5%",
    "+5 to +20%",
    "+20 to +50%",
    ">=+50%",
]

# Tokens accepted by --windows: integer + 'd', 'h', or 'm'.
_WINDOW_RE = re.compile(r"^(\d+)([dhm])$")


def parse_window(s: str) -> tuple[str, int]:
    """Return (label, microseconds) for a token like '30d' / '8h' / '15m'."""
    m = _WINDOW_RE.match(s.strip().lower())
    if not m:
        raise ValueError(f"Bad window token {s!r}; expected e.g. '30d', '8h', '15m'.")
    n, unit = int(m.group(1)), m.group(2)
    if unit == "d":
        return s, n * US_PER_DAY
    elif unit == "h":
        return s, n * US_PER_HOUR
    else:
        return s, n * US_PER_MIN


def bucket_idx(p: float, cutpoints) -> int:
    for i, c in enumerate(cutpoints):
        if p < c:
            return i
    return len(cutpoints)


def per_symbol_imbalance(con, bars_path: str, trips_for_sym: pd.DataFrame,
                         windows: list[tuple[str, int]]) -> pd.DataFrame:
    """For each (label, lookback_us) in `windows`, attach an imbalance column
    `imb_<label>` to the per-symbol trips frame.
    """
    con.execute(f"""
        CREATE OR REPLACE TEMP TABLE bars AS
        SELECT
            start_us,
            SUM(buy_dollar_volume)  OVER (ORDER BY start_us)  AS cum_buy,
            SUM(sell_dollar_volume) OVER (ORDER BY start_us)  AS cum_sell
        FROM read_parquet('{bars_path}')
        WHERE buy_dollar_volume > 0 OR sell_dollar_volume > 0
        ORDER BY start_us;
    """)
    con.register("trips_sym", trips_for_sym[["entry_us"]])

    out = trips_for_sym[["entry_us"]].copy()

    for label, lookback_us in windows:
        # ASOF JOIN twice: at entry, at entry - lookback.
        df = con.execute("""
            WITH at_entry AS (
                SELECT t.entry_us,
                       b.cum_buy AS buy_now,
                       b.cum_sell AS sell_now
                FROM trips_sym t
                ASOF LEFT JOIN bars b
                  ON t.entry_us > b.start_us
            ),
            at_lookback AS (
                SELECT t.entry_us,
                       b.cum_buy AS buy_lb,
                       b.cum_sell AS sell_lb
                FROM trips_sym t
                ASOF LEFT JOIN bars b
                  ON CAST(t.entry_us AS BIGINT) - CAST(? AS BIGINT) > b.start_us
            )
            SELECT ae.entry_us,
                   ae.buy_now - COALESCE(al.buy_lb, 0.0)   AS buy_w,
                   ae.sell_now - COALESCE(al.sell_lb, 0.0) AS sell_w
            FROM at_entry ae
            LEFT JOIN at_lookback al USING (entry_us)
        """, [lookback_us]).fetchdf()
        total = df["buy_w"] + df["sell_w"]
        df[f"imb_{label}"] = (df["buy_w"] - df["sell_w"]) / total
        df.loc[total <= 0, f"imb_{label}"] = pd.NA
        out = out.merge(df[["entry_us", f"imb_{label}"]], on="entry_us", how="left")

    out = trips_for_sym.merge(out, on="entry_us", how="left")
    return out


def print_table(df: pd.DataFrame, label: str, side_str: str):
    sub = df[df["side"] == side_str].dropna(subset=[f"imb_{label}"])
    if len(sub) == 0:
        return
    sub = sub.copy()
    sub["bucket"] = sub[f"imb_{label}"].apply(lambda p: bucket_idx(p, CUTPOINTS))

    side_print = "LONG" if side_str == "long" else "SHORT"
    print(f"--- {side_print} ({len(sub):,} trades) ---")
    print(f"  {'bucket':<14s}  {'trades':>8s}  {'win%':>6s}  "
          f"{'PF':>6s}  {'net_pnl$':>11s}  {'avg_pnl$':>9s}")
    print("  " + "-" * 64)
    for i, blabel in enumerate(BUCKET_LABELS):
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
        print(f"  {blabel:<14s}  {len(b):>8d}  {wr:>5.1f}%  "
              f"{pf_s}  {net_pnl:>+11,.0f}  {avg_pnl:>+9,.2f}")
    total_pnl = sub["net_pnl"].sum()
    total_wins = (sub["net_pnl"] > 0).sum()
    wins_sum = sub.loc[sub["net_pnl"] > 0, "net_pnl"].sum()
    losses_sum = -sub.loc[sub["net_pnl"] < 0, "net_pnl"].sum()
    pf = (wins_sum / losses_sum) if losses_sum > 0 else 0.0
    wr = 100.0 * total_wins / len(sub)
    print("  " + "-" * 64)
    print(f"  {'TOTAL':<14s}  {len(sub):>8d}  {wr:>5.1f}%  "
          f"{pf:>6.2f}  {total_pnl:>+11,.0f}  {sub['net_pnl'].mean():>+9,.2f}")


def print_decile_table(df: pd.DataFrame, label: str, side_str: str, n_buckets: int = 10):
    """Decile-by-imbalance breakdown — equal-count slices over the imb_<label>
    column, restricted to one trade side. Prints the imbalance range per
    decile so the per-side regimes are readable."""
    sub = df[df["side"] == side_str].dropna(subset=[f"imb_{label}"]).copy()
    if len(sub) == 0:
        return
    # NTILE-equivalent: rank, then bucket via (rank-1)*n_buckets//len.
    sub = sub.sort_values(f"imb_{label}").reset_index(drop=True)
    sub["bucket"] = (sub.index * n_buckets // len(sub))

    side_print = "LONG" if side_str == "long" else "SHORT"
    print(f"--- {side_print} ({len(sub):,} trades) ---")
    print(f"  {'decile':>6s}  {'imb_lo':>8s}  {'imb_hi':>8s}  "
          f"{'trades':>7s}  {'win%':>6s}  {'PF':>6s}  "
          f"{'net_pnl$':>11s}  {'avg_pnl$':>9s}")
    print("  " + "-" * 76)
    for i in range(n_buckets):
        b = sub[sub["bucket"] == i]
        if len(b) == 0:
            continue
        imb_lo = b[f"imb_{label}"].min() * 100.0
        imb_hi = b[f"imb_{label}"].max() * 100.0
        wins = (b["net_pnl"] > 0).sum()
        losses_sum = -b.loc[b["net_pnl"] < 0, "net_pnl"].sum()
        wins_sum = b.loc[b["net_pnl"] > 0, "net_pnl"].sum()
        pf = (wins_sum / losses_sum) if losses_sum > 0 \
             else float("inf") if wins_sum > 0 else 0.0
        avg_pnl = b["net_pnl"].mean()
        net_pnl = b["net_pnl"].sum()
        wr = 100.0 * wins / len(b)
        pf_s = f"{pf:>6.2f}" if pf != float("inf") else "   inf"
        print(f"  {i+1:>6d}  {imb_lo:>+7.2f}%  {imb_hi:>+7.2f}%  "
              f"{len(b):>7d}  {wr:>5.1f}%  {pf_s}  "
              f"{net_pnl:>+11,.0f}  {avg_pnl:>+9,.2f}")
    total_pnl = sub["net_pnl"].sum()
    total_wins = (sub["net_pnl"] > 0).sum()
    wins_sum = sub.loc[sub["net_pnl"] > 0, "net_pnl"].sum()
    losses_sum = -sub.loc[sub["net_pnl"] < 0, "net_pnl"].sum()
    pf = (wins_sum / losses_sum) if losses_sum > 0 else 0.0
    wr = 100.0 * total_wins / len(sub)
    print("  " + "-" * 76)
    print(f"  {'TOTAL':>6s}  {'':>8s}  {'':>8s}  "
          f"{len(sub):>7d}  {wr:>5.1f}%  {pf:>6.2f}  "
          f"{total_pnl:>+11,.0f}  {sub['net_pnl'].mean():>+9,.2f}")


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--trips", default=DEFAULT_TRIPS)
    ap.add_argument("--bars-root", default=DEFAULT_BARS_ROOT)
    ap.add_argument("--windows", nargs="+",
                    default=["30d", "200h", "24h", "16h", "8h"],
                    help="List of trailing-window tokens (e.g. 30d 200h 24h).")
    ap.add_argument("--deciles", action="store_true",
                    help="Use NTILE(10) per-side equal-count buckets instead "
                         "of fixed-cutpoint buckets. Useful when most trades "
                         "concentrate near zero and the cutpoint buckets get "
                         "tiny extreme-bucket samples.")
    ap.add_argument("--n-buckets", type=int, default=10,
                    help="Number of buckets when --deciles. Default: 10.")
    args = ap.parse_args()

    repo_root = os.path.abspath(os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "..", ".."))
    trips_path = os.path.join(repo_root, args.trips)
    bars_root = os.path.join(repo_root, args.bars_root)

    windows = [parse_window(w) for w in args.windows]
    print(f"Windows: {', '.join(w[0] for w in windows)}")

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
        result = per_symbol_imbalance(con, bars_path, sub, windows)
        pieces.append(result)
        if i % 50 == 0 or i == len(by_symbol):
            print(f"  [{i:>4d}/{len(by_symbol)}] processed", file=sys.stderr)

    if n_missing > 0:
        print(f"  ({n_missing} symbols had no parquet, skipped)")

    df = pd.concat(pieces, ignore_index=True)
    print()

    for label, _ in windows:
        n_kept = df[f"imb_{label}"].notna().sum()
        print(f"=== Window: {label}  ({n_kept:,} of {len(df):,} trades) ===")
        print()
        if args.deciles:
            print_decile_table(df, label, "long", args.n_buckets)
            print()
            print_decile_table(df, label, "short", args.n_buckets)
        else:
            print_table(df, label, "long")
            print()
            print_table(df, label, "short")
        print()


if __name__ == "__main__":
    main()
