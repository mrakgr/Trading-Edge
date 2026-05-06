"""Per-quarter P&L breakdown for the orderflow-MA backtest variants.

For each system listed in SYSTEMS, computes per-quarter (UTC) net P&L from
the trips CSV (bucketing by exit-day quarter). Outputs a single wide table:

    quarter        v0        cumsum_vt  z_persist  z_volstop100  z_vwapstop
    2024 Q2        ...
    ...

A second table shows per-month P&L for finer-grained inspection of where
the stop variants bleed.

Use:

    python scripts/crypto/time_distribution.py
"""

import os

import duckdb
import pandas as pd


SYSTEMS = [
    ("v0_1h",         "data/crypto/v0_compare/backtest_results_trips_1h_ma200h_ls.csv"),
    ("cumsum_vt",     "data/crypto/cumsum_voltuned/backtest_results_trips_1m_th60_ls.csv"),
    ("z_persist",     "data/crypto/cumsum_z_persistexit/backtest_results_trips_1m_th15_ls.csv"),
    ("z_volstop100",  "data/crypto/cumsum_z_volstop100/backtest_results_trips_1m_th15_ls.csv"),
    ("z_vwapstop",    "data/crypto/cumsum_z_vwapstop200/backtest_results_trips_1m_th15_ls.csv"),
]


def per_period_pnl(path: str, period_sql: str) -> pd.DataFrame:
    """Group trips by `period_sql` (a DuckDB expression returning a label)
    and sum net_pnl. Returns DataFrame with columns: period, net_pnl."""
    con = duckdb.connect()
    return con.execute(f"""
        SELECT
            {period_sql} AS period,
            SUM(net_pnl) AS net_pnl
        FROM read_csv_auto('{path}')
        GROUP BY 1
        ORDER BY 1
    """).fetchdf()


def main():
    repo_root = os.path.abspath(os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "..", ".."))

    for period_name, period_sql in [
        ("Quarter",
         "strftime(make_timestamp(exit_us), '%Y') || ' Q' || "
         "CASE "
         "  WHEN CAST(strftime(make_timestamp(exit_us), '%m') AS INTEGER) <= 3 THEN '1' "
         "  WHEN CAST(strftime(make_timestamp(exit_us), '%m') AS INTEGER) <= 6 THEN '2' "
         "  WHEN CAST(strftime(make_timestamp(exit_us), '%m') AS INTEGER) <= 9 THEN '3' "
         "  ELSE '4' "
         "END"),
        ("Month",
         "strftime(make_timestamp(exit_us), '%Y-%m')"),
    ]:
        wide = None
        labels = []
        for label, rel_path in SYSTEMS:
            path = os.path.join(repo_root, rel_path)
            if not os.path.exists(path):
                continue
            df = per_period_pnl(path, period_sql)
            df = df.rename(columns={"net_pnl": label})
            labels.append(label)
            if wide is None:
                wide = df
            else:
                wide = wide.merge(df, on="period", how="outer")

        wide = wide.fillna(0).sort_values("period").reset_index(drop=True)

        # Format as dollar figures.
        formatted = wide.copy()
        for col in labels:
            formatted[col] = formatted[col].apply(lambda x: f"{x:>+10,.0f}")

        print()
        print(f"=== {period_name} (USDT, $1000 notional/trade) ===")
        print()
        # Print as fixed-width columns.
        col_widths = {}
        col_widths["period"] = max(8, formatted["period"].str.len().max())
        for col in labels:
            col_widths[col] = max(len(col), formatted[col].str.len().max())
        # Header
        header = " | ".join(c.ljust(col_widths[c]) for c in formatted.columns)
        print(header)
        print("-" * len(header))
        for _, row in formatted.iterrows():
            print(" | ".join(str(row[c]).ljust(col_widths[c])
                             for c in formatted.columns))

        # Totals row
        print("-" * len(header))
        totals = ["TOTAL".ljust(col_widths["period"])]
        for col in labels:
            tot = wide[col].sum()
            totals.append(f"{tot:>+10,.0f}".ljust(col_widths[col]))
        print(" | ".join(totals))


if __name__ == "__main__":
    main()
