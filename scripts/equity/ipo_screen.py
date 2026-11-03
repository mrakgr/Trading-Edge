"""IPO-like first days (user 2026-11-03, the realized-spread study): new listings since 2020 (ipo_candidates.parquet: a CS
ticker's first daily row with no row in the 45 days before, first-day dollar volume >= $100M) whose FIRST regular trade
comes after 10:00 ET — the IPO / direct-listing cross; spinoffs, renames and de-SPACs open at 09:30.
  cd research && python -u scripts/equity/ipo_screen.py --min-dv 3e8 > logs/ipo_screen.log 2>&1
"""
import argparse, datetime, os, zoneinfo, duckdb
ap = argparse.ArgumentParser(); ap.add_argument('--min-dv', type=float, default=3e8); ap.add_argument('--trades', default='data/bulk/trades')
a = ap.parse_args()
NY = zoneinfo.ZoneInfo('America/New_York')
EXCL = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53, 8, 15, 19]::UTINYINT[]"
con = duckdb.connect(); con.execute("SET memory_limit='2GB'; SET threads=4")
C = con.execute(f"SELECT ticker, date, dv FROM 'data/equity/realized_spread/ipo_candidates.parquet' WHERE dv >= {a.min_dv} ORDER BY dv DESC").df()
rows = []
for r in C.itertuples():
    d = str(r.date)[:10]; f = f'{a.trades}/{d}.parquet'
    if not os.path.exists(f): rows.append((r.ticker, d, r.dv, None, 'no tape')); continue
    t0 = con.execute(f"SELECT min(sip_timestamp) FROM read_parquet('{f}') WHERE ticker = '{r.ticker}' AND size > 0 "
                     f"AND NOT list_has_any(conditions, {EXCL}) AND exchange <> 4").fetchone()[0]
    if t0 is None: rows.append((r.ticker, d, r.dv, None, 'no trades')); continue
    tt = datetime.datetime.fromtimestamp(t0 / 1e9, NY)
    rows.append((r.ticker, d, r.dv, tt.strftime('%H:%M:%S'), 'ipo' if tt.hour * 60 + tt.minute >= 10 * 60 + 5 else 'open'))
    print(f'{r.ticker:6s} {d} {r.dv / 1e6:9,.0f} M  first lit trade {rows[-1][3]}  {rows[-1][4]}', flush=True)
import pandas as pd
pd.DataFrame(rows, columns=['ticker', 'date', 'dv', 'first_lit', 'kind']).to_parquet('data/equity/realized_spread/ipo_screen.parquet', index=False)
