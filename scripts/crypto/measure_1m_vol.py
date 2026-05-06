"""Measure the empirical distribution of 1m bar log-return rolling std,
window = 7 days (10080 bars), across a basket of representative perps.

Used to set `referenceVolPct` for the 1m cumsum-sweep. The engine's
vol-sizing formula is

    effectiveNotional = min(notional, notional * referenceVol / barVol)

so `referenceVol` should be roughly the *typical* per-bar log-return std at
the timeframe. At 1h v0 used 1% (which matched p50-ish 1h vol). We need the
1m equivalent.

For each symbol, we compute the rolling 10080-bar sample std of log(close /
prevClose), drop pre-fill rows, and report percentiles. Reported in BOTH
decimal and bps-per-minute units for readability.
"""

import argparse
import duckdb
import numpy as np
import pandas as pd


DEFAULT_BASKET = [
    # Tier-A liquidity (long-only side traded at v0's $28.8M ADV gate)
    "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT",
    # Mid-tier
    "AVAXUSDT", "LINKUSDT", "DOGEUSDT", "ADAUSDT",
    # High-beta / meme-coin band where vol is notoriously bursty
    "PEPEUSDT", "WIFUSDT",
]


def measure_symbol(bars_root: str, symbol: str, window_bars: int) -> pd.DataFrame:
    path = f"{bars_root}/1m/{symbol}.parquet"
    con = duckdb.connect()
    # Read ordered close prices, compute log-return, then rolling sample std
    # via a window function. DuckDB's stddev_samp is sample std (N-1 divisor),
    # which matches StdMa.SampleStd in the engine.
    df = con.execute(f"""
        WITH bars AS (
          SELECT start_us, close,
                 LAG(close) OVER (ORDER BY start_us) AS prev_close
          FROM read_parquet('{path}')
        ),
        rets AS (
          SELECT start_us,
                 LN(close / prev_close) AS r
          FROM bars
          WHERE prev_close IS NOT NULL AND prev_close > 0 AND close > 0
        ),
        rolling AS (
          SELECT start_us,
                 stddev_samp(r) OVER (
                     ORDER BY start_us
                     ROWS BETWEEN {window_bars - 1} PRECEDING AND CURRENT ROW
                 ) AS std_{window_bars}
          FROM rets
        )
        SELECT start_us, std_{window_bars} AS std_w
        FROM rolling
        WHERE std_{window_bars} IS NOT NULL
        ORDER BY start_us
    """).fetchdf()
    return df


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--bars-root",
                    default="/home/mrakgr/Trading-Edge/data/crypto/perps_bars",
                    help="Root containing 1m/<symbol>.parquet")
    ap.add_argument("--window-bars", type=int, default=10080,
                    help="Rolling window in 1m bars. Default 10080 = 7 days.")
    ap.add_argument("--symbols", nargs="+", default=DEFAULT_BASKET,
                    help="Symbols to measure")
    args = ap.parse_args()

    print(f"Rolling-std window: {args.window_bars:,} 1m bars "
          f"({args.window_bars / 1440:.1f} days)")
    print(f"Bars root: {args.bars_root}")
    print()
    print(f"{'symbol':<14} {'n_bars':>10} {'p10':>10} {'p25':>10} "
          f"{'med':>10} {'p75':>10} {'p90':>10} {'p95':>10} {'p99':>10}")
    print("-" * 110)

    all_stds = []
    for sym in args.symbols:
        try:
            df = measure_symbol(args.bars_root, sym, args.window_bars)
        except Exception as e:
            print(f"{sym:<14} FAILED: {e}")
            continue
        if len(df) == 0:
            print(f"{sym:<14} (no data)")
            continue
        s = df["std_w"].values
        all_stds.append(s)
        ps = np.percentile(s, [10, 25, 50, 75, 90, 95, 99])
        print(f"{sym:<14} {len(s):>10,} "
              f"{ps[0]*1e4:>9.2f}b {ps[1]*1e4:>9.2f}b {ps[2]*1e4:>9.2f}b "
              f"{ps[3]*1e4:>9.2f}b {ps[4]*1e4:>9.2f}b {ps[5]*1e4:>9.2f}b "
              f"{ps[6]*1e4:>9.2f}b")

    if all_stds:
        pooled = np.concatenate(all_stds)
        ps = np.percentile(pooled, [10, 25, 50, 75, 90, 95, 99])
        print("-" * 110)
        print(f"{'POOLED':<14} {len(pooled):>10,} "
              f"{ps[0]*1e4:>9.2f}b {ps[1]*1e4:>9.2f}b {ps[2]*1e4:>9.2f}b "
              f"{ps[3]*1e4:>9.2f}b {ps[4]*1e4:>9.2f}b {ps[5]*1e4:>9.2f}b "
              f"{ps[6]*1e4:>9.2f}b")
        print()
        print("Units: bps/minute (= rolling-window log-return sample std).")
        print(f"Decimal-fraction values for engine config (--reference-vol-pct):")
        print(f"  median   = {ps[2]:.6f}  (--reference-vol-pct {ps[2]*100:.4f})")
        print(f"  p75      = {ps[3]:.6f}  (--reference-vol-pct {ps[3]*100:.4f})")
        print(f"  p90      = {ps[4]:.6f}  (--reference-vol-pct {ps[4]*100:.4f})")
        print(f"  p95      = {ps[5]:.6f}  (--reference-vol-pct {ps[5]*100:.4f})")
        print()
        print("Recommended starting point: pick around the median; that's the")
        print("typical bar-vol the engine sees. Trades fired during higher-than-")
        print("typical vol regimes will get downsized below full notional.")


if __name__ == "__main__":
    main()
