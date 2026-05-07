"""Drill into the high-rvol short buckets (5-10x and >=10x) on the
30d/8h volume-ratio stratification.

Asks the questions:
  - PF / win-rate / avg-trade per bucket (sanity, should match the table)
  - hold-time distribution
  - MFE / MAE distribution and ratios
  - top-N biggest losers (these drag the bucket PF down)
  - top-N biggest winners (does the edge come from a few outliers?)
  - distribution by symbol — is it concentrated?
  - mean-reversion check: does net PnL skew with the size of the
    favorable initial move (MFE) vs adverse (MAE)?

Plus exports a "to-chart" CSV listing the trades worth visual review
(top 25 losers + top 25 winners) so we can chart them next.
"""

import argparse
import os

import numpy as np
import pandas as pd


DEFAULT_TRIPS = "data/crypto/cumsum_z_persistexit/trips_th15_volratio_30d8h.csv"


def fmt_pct(x):
    return f"{100.0 * x:>5.1f}%"


def bucket_table(df: pd.DataFrame, label: str):
    """Sanity table: PF/winrate/avg/sum per bucket."""
    print(f"=== {label} ({len(df):,} trades) ===")
    print(f"  trades={len(df)}  net_pnl={df['net_pnl'].sum():+,.0f}  "
          f"avg={df['net_pnl'].mean():+,.2f}")
    wins = df[df.net_pnl > 0]; losses = df[df.net_pnl < 0]
    pf = wins.net_pnl.sum() / -losses.net_pnl.sum() if len(losses) else float("inf")
    wr = len(wins) / len(df)
    print(f"  PF={pf:.2f}  WR={fmt_pct(wr)}  ({len(wins)}W / {len(losses)}L)")
    print(f"  bars_held: med={df.bars_held.median():.0f}  "
          f"p25={df.bars_held.quantile(.25):.0f}  "
          f"p75={df.bars_held.quantile(.75):.0f}  "
          f"p95={df.bars_held.quantile(.95):.0f}  "
          f"max={df.bars_held.max():.0f}")
    # MFE/MAE in bps; in the trips CSV they are already signed/percentage
    # checks: the v0 trip writer stores them as bps relative to entry.
    print(f"  MFE bps: med={df.mfe.median():.1f}  p75={df.mfe.quantile(.75):.1f}  "
          f"p95={df.mfe.quantile(.95):.1f}  max={df.mfe.max():.1f}")
    print(f"  MAE bps: med={df.mae.median():.1f}  p25={df.mae.quantile(.25):.1f}  "
          f"p05={df.mae.quantile(.05):.1f}  min={df.mae.min():.1f}")
    print()


def top_movers(df: pd.DataFrame, n: int, label: str):
    print(f"--- {label}: top {n} biggest losers ---")
    cols = ["symbol", "entry_dt", "exit_dt", "side", "ratio",
            "bars_held", "mfe", "mae", "net_pnl"]
    losers = df.nsmallest(n, "net_pnl")[cols]
    print(losers.to_string(index=False,
                           formatters={"ratio": "{:.2f}".format,
                                       "mfe":   "{:+.1f}".format,
                                       "mae":   "{:+.1f}".format,
                                       "net_pnl": "{:+,.2f}".format}))
    print()
    print(f"--- {label}: top {n} biggest winners ---")
    winners = df.nlargest(n, "net_pnl")[cols]
    print(winners.to_string(index=False,
                            formatters={"ratio": "{:.2f}".format,
                                        "mfe":   "{:+.1f}".format,
                                        "mae":   "{:+.1f}".format,
                                        "net_pnl": "{:+,.2f}".format}))
    print()


def symbol_concentration(df: pd.DataFrame, label: str, top_n=12):
    print(f"--- {label}: symbol concentration ---")
    g = df.groupby("symbol").agg(
        trades=("net_pnl", "size"),
        net_pnl=("net_pnl", "sum"),
        wins=("net_pnl", lambda s: int((s > 0).sum())),
    ).sort_values("net_pnl")
    print(f"  total symbols: {len(g)}")
    print("  worst symbols by net pnl:")
    print(g.head(top_n).to_string(formatters={"net_pnl": "{:+,.2f}".format}))
    print("  best symbols by net pnl:")
    print(g.tail(top_n).to_string(formatters={"net_pnl": "{:+,.2f}".format}))
    print()


def mfe_mae_dynamics(df: pd.DataFrame, label: str):
    """Are losers trades that *immediately* went the wrong way (no
    favorable MFE before the MAE) vs trades that gave us a paper
    profit and then reversed? The two patterns ask for different
    fixes (entry quality vs exit/stop logic)."""
    print(f"--- {label}: MFE vs net pnl dynamics ---")
    losers = df[df.net_pnl < 0]
    winners = df[df.net_pnl > 0]
    print(f"  losers n={len(losers)}  median MFE={losers.mfe.median():.1f}bp  "
          f"median MAE={losers.mae.median():.1f}bp  "
          f"median bars={losers.bars_held.median():.0f}")
    print(f"  winners n={len(winners)}  median MFE={winners.mfe.median():.1f}bp  "
          f"median MAE={winners.mae.median():.1f}bp  "
          f"median bars={winners.bars_held.median():.0f}")
    # Among losers, fraction that had a "real" paper profit — say MFE > 50bp —
    # before going adverse. These are the trades a tighter trailing
    # stop would have saved.
    if len(losers):
        had_50bp = losers[losers.mfe > 50]
        had_100bp = losers[losers.mfe > 100]
        print(f"  losers with MFE > 50bp:  {len(had_50bp)}/{len(losers)} "
              f"({100.0*len(had_50bp)/len(losers):.0f}%)  "
              f"sum_pnl={had_50bp.net_pnl.sum():+,.0f}")
        print(f"  losers with MFE > 100bp: {len(had_100bp)}/{len(losers)} "
              f"({100.0*len(had_100bp)/len(losers):.0f}%)  "
              f"sum_pnl={had_100bp.net_pnl.sum():+,.0f}")
    print()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--trips", default=DEFAULT_TRIPS)
    ap.add_argument("--out-dir", default="data/crypto/inspect_rvol")
    ap.add_argument("--top-n", type=int, default=20)
    args = ap.parse_args()

    repo = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
    trips_path = os.path.join(repo, args.trips) if not os.path.isabs(args.trips) else args.trips
    out_dir = os.path.join(repo, args.out_dir) if not os.path.isabs(args.out_dir) else args.out_dir
    os.makedirs(out_dir, exist_ok=True)

    df = pd.read_csv(trips_path)
    df["entry_dt"] = pd.to_datetime(df.entry_us, unit="us", utc=True).dt.strftime("%Y-%m-%d %H:%M")
    df["exit_dt"]  = pd.to_datetime(df.exit_us,  unit="us", utc=True).dt.strftime("%Y-%m-%d %H:%M")

    # only shorts in the 5-10x and >=10x buckets
    short = df[df.side == "short"]
    bucket_5_10 = short[(short.ratio >= 5.0) & (short.ratio < 10.0)].copy()
    bucket_10p  = short[short.ratio >= 10.0].copy()

    bucket_table(bucket_5_10, "SHORT 5-10x")
    bucket_table(bucket_10p,  "SHORT >=10x")
    # also a comparison: the 3-5x bucket which had the higher PF in our prior tables
    bucket_3_5 = short[(short.ratio >= 3.0) & (short.ratio < 5.0)].copy()
    bucket_table(bucket_3_5, "SHORT 3-5x  (for comparison)")

    print("=" * 70)
    print("BIGGEST MOVERS")
    print("=" * 70)
    top_movers(bucket_5_10, args.top_n, "SHORT 5-10x")
    top_movers(bucket_10p,  args.top_n, "SHORT >=10x")

    print("=" * 70)
    print("MFE/MAE DYNAMICS")
    print("=" * 70)
    mfe_mae_dynamics(bucket_5_10, "SHORT 5-10x")
    mfe_mae_dynamics(bucket_10p,  "SHORT >=10x")
    mfe_mae_dynamics(bucket_3_5,  "SHORT 3-5x  (for comparison)")

    print("=" * 70)
    print("SYMBOL CONCENTRATION")
    print("=" * 70)
    symbol_concentration(bucket_5_10, "SHORT 5-10x")
    symbol_concentration(bucket_10p,  "SHORT >=10x")

    # write to-chart shortlist: top losers + top winners from the two buckets
    chart_set = pd.concat([
        bucket_5_10.nsmallest(args.top_n, "net_pnl").assign(bucket="5-10x", reason="loser"),
        bucket_5_10.nlargest(args.top_n, "net_pnl").assign(bucket="5-10x", reason="winner"),
        bucket_10p.nsmallest(args.top_n, "net_pnl").assign(bucket=">=10x", reason="loser"),
        bucket_10p.nlargest(args.top_n, "net_pnl").assign(bucket=">=10x", reason="winner"),
    ], ignore_index=True)
    out = os.path.join(out_dir, "highrvol_shorts_to_chart.csv")
    cols = ["bucket", "reason", "symbol", "entry_dt", "exit_dt",
            "entry_price", "exit_price", "ratio", "bars_held",
            "mfe", "mae", "net_pnl", "effective_notional",
            "entry_us", "exit_us"]
    chart_set[cols].to_csv(out, index=False)
    print(f"Wrote chart shortlist ({len(chart_set)} rows) to {out}")


if __name__ == "__main__":
    main()
