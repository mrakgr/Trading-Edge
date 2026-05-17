"""Cross-reference VWAP-flip trades against the S&P 500 breadth imbalance
at trade entry.

For each trade in data/vwap/trades_SPY_*.csv, look up the breadth imbalance
at (entry_date, entry_bucket) from data/breadth/imbalance_vwma_4m.parquet,
then group trades by:
  (a) longs only, by imbalance decile
  (b) shorts only, by imbalance decile
  (c) all trades, by imbalance decile (for contrast)

Reports per-bucket: count, win rate, mean P&L, total P&L, profit factor.

The hypothesis worth testing: longs entered while breadth is strongly
positive (imbalance > +0.5) should outperform longs entered into negative
breadth; shorts entered into negative breadth should outperform shorts
entered into positive breadth.
"""
from __future__ import annotations

import argparse
import sys

import duckdb

DEFAULT_TRADES = '/home/mrakgr/Trading-Edge/data/vwap/trades_SPY_2024-01-01_2026-05-17.csv'
DEFAULT_BREADTH = '/home/mrakgr/Trading-Edge/data/breadth/imbalance_vwma_4m.parquet'
N_BUCKETS = 10


def fmt_pf(gw: float, gl: float) -> str:
    if gl == 0:
        return 'inf' if gw > 0 else '0.00'
    return f'{gw / gl:.2f}'


def summarize(con: duckdb.DuckDBPyConnection, title: str, side_filter: str) -> None:
    """side_filter: '' for all trades, "AND side = 'long'" or "AND side = 'short'"."""
    # NTILE buckets imbalance into N_BUCKETS roughly-equal-size groups so each
    # decile gets enough trades to be readable. The bucket edges depend on the
    # filtered subset, which is exactly what we want when comparing longs vs.
    # shorts separately (each side gets its own deciles).
    sql = f"""
    WITH joined AS (
        SELECT t.side, t.pnl, b.imbalance
        FROM trades t
        JOIN breadth b
          ON b.date = t.entry_date AND b.bucket = t.entry_bucket
        WHERE 1=1 {side_filter}
    ),
    binned AS (
        SELECT *,
               NTILE({N_BUCKETS}) OVER (ORDER BY imbalance) AS decile
        FROM joined
    )
    SELECT
        decile,
        COUNT(*) AS n,
        ROUND(MIN(imbalance), 3) AS imb_lo,
        ROUND(MAX(imbalance), 3) AS imb_hi,
        ROUND(AVG(imbalance), 3) AS imb_mean,
        ROUND(100.0 * AVG(CASE WHEN pnl > 0 THEN 1.0 ELSE 0.0 END), 1) AS win_pct,
        ROUND(AVG(pnl), 4) AS mean_pnl,
        ROUND(SUM(pnl), 2) AS total_pnl,
        ROUND(SUM(CASE WHEN pnl > 0 THEN pnl ELSE 0 END), 2) AS gross_win,
        ROUND(SUM(CASE WHEN pnl < 0 THEN -pnl ELSE 0 END), 2) AS gross_loss
    FROM binned
    GROUP BY decile
    ORDER BY decile;
    """
    rows = con.execute(sql).fetchall()
    if not rows:
        print(f'\n{title}: no trades matched.', file=sys.stderr)
        return

    print(f'\n{title}  (deciles, N_BUCKETS={N_BUCKETS}):')
    print(f'  {"#":>2}  {"n":>5}  {"imb_lo":>7}  {"imb_hi":>7}  {"imb_mu":>7}  '
          f'{"win%":>5}  {"meanPnL":>9}  {"totalPnL":>10}  {"PF":>6}')
    print('  ' + '-' * 80)
    total_n = 0
    total_pnl = 0.0
    total_gw = 0.0
    total_gl = 0.0
    for (dec, n, lo, hi, mu, win, mp, tp, gw, gl) in rows:
        pf = fmt_pf(gw, gl)
        print(f'  {dec:>2}  {n:>5}  {lo:>7.3f}  {hi:>7.3f}  {mu:>7.3f}  '
              f'{win:>5.1f}  {mp:>+9.4f}  {tp:>+10.2f}  {pf:>6}')
        total_n += n
        total_pnl += tp
        total_gw += gw
        total_gl += gl
    print('  ' + '-' * 80)
    pf_total = fmt_pf(total_gw, total_gl)
    print(f'  {"ALL":>2}  {total_n:>5}  {"":>7}  {"":>7}  {"":>7}  '
          f'{"":>5}  {"":>9}  {total_pnl:>+10.2f}  {pf_total:>6}')


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument('--trades', default=DEFAULT_TRADES)
    p.add_argument('--breadth', default=DEFAULT_BREADTH)
    args = p.parse_args()

    con = duckdb.connect(':memory:')
    con.execute(f"""
        CREATE VIEW trades AS
        SELECT CAST(entry_date AS DATE) AS entry_date,
               entry_bucket::INTEGER AS entry_bucket,
               side,
               pnl
        FROM read_csv('{args.trades}', header = true);
    """)
    con.execute(f"""
        CREATE VIEW breadth AS
        SELECT date, bucket, imbalance
        FROM read_parquet('{args.breadth}');
    """)

    overall = con.execute("""
        SELECT
            COUNT(*) AS n,
            SUM(CASE WHEN side='long'  THEN 1 ELSE 0 END) AS n_long,
            SUM(CASE WHEN side='short' THEN 1 ELSE 0 END) AS n_short,
            ROUND(SUM(pnl), 2)                                   AS total_pnl,
            ROUND(SUM(CASE WHEN side='long'  THEN pnl ELSE 0 END), 2) AS long_pnl,
            ROUND(SUM(CASE WHEN side='short' THEN pnl ELSE 0 END), 2) AS short_pnl
        FROM trades
        WHERE EXISTS (SELECT 1 FROM breadth b WHERE b.date = trades.entry_date AND b.bucket = trades.entry_bucket);
    """).fetchone()
    print(f'Trades joined to breadth: {overall[0]:,} '
          f'(long {overall[1]:,}, short {overall[2]:,})')
    print(f'Total P&L:     {overall[3]:+.2f}/share  '
          f'(long {overall[4]:+.2f}, short {overall[5]:+.2f})')

    summarize(con, 'LONGS  by imbalance at entry',  "AND side = 'long'")
    summarize(con, 'SHORTS by imbalance at entry',  "AND side = 'short'")
    summarize(con, 'ALL    by imbalance at entry',  '')

    return 0


if __name__ == '__main__':
    sys.exit(main())
