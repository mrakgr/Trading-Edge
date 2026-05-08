"""Quintile-bin a loose-gate ExtremeRvol trip CSV by rvol-at-entry x
price-rise-at-entry, then tabulate PF / netPnL / trade-count per 5x5 cell.

Run:
    python scripts/crypto/quintile_bin_extreme_rvol.py \
        data/crypto/extreme_rvol_quintile/results_trips_1m_rvol8_pr0.05_ts90m_ed0.2_short.csv

Reads the trip CSV (must have ratio_at_entry and price_rise_at_entry columns,
populated by the new RoundTrip schema). Computes the 5-quantile cutoffs for
each axis empirically across the trade population, bins each trade into a
(rvol_q, pr_q) cell, and reports:

    1. PF table (5x5)
    2. Trade-count table
    3. Net-pnl table
    4. The cutoff values themselves (so the user knows what the bins mean)
"""

import sys
from pathlib import Path

import duckdb
import pandas as pd


def quintile_breakdown(trips_csv: str) -> None:
    con = duckdb.connect()
    # abs(price_rise_at_entry) so the long engine (which writes negative
    # values for declines) bins by magnitude alongside the short engine.
    trips = con.execute(
        f"""
        SELECT
            ratio_at_entry           AS rvol,
            abs(price_rise_at_entry) AS pr,
            net_pnl
        FROM read_csv_auto('{trips_csv}', HEADER=TRUE)
        WHERE side IN ('short', 'long')
        """
    ).df()

    n = len(trips)
    if n == 0:
        print(f"No trades in {trips_csv}")
        return

    # Empirical quintile cutoffs: 0%, 20%, 40%, 60%, 80%, 100%.
    rvol_cuts = trips["rvol"].quantile([0.0, 0.2, 0.4, 0.6, 0.8, 1.0]).tolist()
    pr_cuts   = trips["pr"  ].quantile([0.0, 0.2, 0.4, 0.6, 0.8, 1.0]).tolist()

    # Bucket via pd.cut with include_lowest so the min boundary is inclusive.
    # Labels 0..4 — Q1 is the lowest quintile.
    rvol_q = pd.cut(trips["rvol"], bins=rvol_cuts, labels=False, include_lowest=True)
    pr_q   = pd.cut(trips["pr"  ], bins=pr_cuts  , labels=False, include_lowest=True)
    trips["rvol_q"] = rvol_q
    trips["pr_q"]   = pr_q

    def fmt_table(values, fmt: str, fill: str = "    .   ") -> str:
        # values: dict-of-dicts {pr_q -> {rvol_q -> v}} effectively a pivot.
        lines = []
        header = "        " + " ".join(f" rvolQ{c+1}  " for c in range(5))
        lines.append(header)
        lines.append("       " + "-" * (8 * 5 + 1))
        for pr in range(5):
            row = [f"prQ{pr+1}  |"]
            for rv in range(5):
                v = values.get((rv, pr))
                row.append((fmt % v) if v is not None else fill)
            lines.append(" ".join(row))
        return "\n".join(lines)

    # PF table.
    pf_map = {}
    cnt_map = {}
    pnl_map = {}
    for (rv, pr), grp in trips.groupby(["rvol_q", "pr_q"]):
        gross_w = grp.loc[grp["net_pnl"] > 0, "net_pnl"].sum()
        gross_l = -grp.loc[grp["net_pnl"] < 0, "net_pnl"].sum()
        pf = (gross_w / gross_l) if gross_l > 0 else float("inf") if gross_w > 0 else 0.0
        pf_map[(int(rv), int(pr))]  = pf
        cnt_map[(int(rv), int(pr))] = int(len(grp))
        pnl_map[(int(rv), int(pr))] = float(grp["net_pnl"].sum())

    print(f"Loose-gate ExtremeRvol trip CSV: {trips_csv}")
    print(f"Trade count: {n}")
    print()
    print("Quintile cutoffs (rvol — log scale recommended for interpretation):")
    print("  " + "  ".join(f"{c:6.2f}" for c in rvol_cuts))
    print("Quintile cutoffs (price rise, fractional):")
    print("  " + "  ".join(f"{c:6.3f}" for c in pr_cuts))
    print()

    print("=== Profit Factor (5x5: rvol-quintile cols, pr-quintile rows) ===")
    print(fmt_table(pf_map, "%6.2f  "))
    print()
    print("=== Trade Count ===")
    print(fmt_table(cnt_map, "%6d  "))
    print()
    print("=== Net P&L (USDT, $1k notional) ===")
    print(fmt_table(pnl_map, "%6.0f  "))
    print()

    # Marginal totals for sanity.
    print("=== Marginal PF by rvol quintile (pooled across pr) ===")
    for rv in range(5):
        sub = trips[trips["rvol_q"] == rv]
        if len(sub) == 0:
            print(f"  rvolQ{rv+1}: no trades")
            continue
        gross_w = sub.loc[sub["net_pnl"] > 0, "net_pnl"].sum()
        gross_l = -sub.loc[sub["net_pnl"] < 0, "net_pnl"].sum()
        pf = (gross_w / gross_l) if gross_l > 0 else float("inf") if gross_w > 0 else 0.0
        net = sub["net_pnl"].sum()
        print(f"  rvolQ{rv+1}: PF={pf:5.2f}  trips={len(sub):5d}  netPnL=${net:9.0f}")

    print()
    print("=== Marginal PF by price-rise quintile (pooled across rvol) ===")
    for pr in range(5):
        sub = trips[trips["pr_q"] == pr]
        if len(sub) == 0:
            print(f"  prQ{pr+1}: no trades")
            continue
        gross_w = sub.loc[sub["net_pnl"] > 0, "net_pnl"].sum()
        gross_l = -sub.loc[sub["net_pnl"] < 0, "net_pnl"].sum()
        pf = (gross_w / gross_l) if gross_l > 0 else float("inf") if gross_w > 0 else 0.0
        net = sub["net_pnl"].sum()
        print(f"  prQ{pr+1}: PF={pf:5.2f}  trips={len(sub):5d}  netPnL=${net:9.0f}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("usage: quintile_bin_extreme_rvol.py <trips.csv>")
    quintile_breakdown(sys.argv[1])
