#!/usr/bin/env python3
"""The 0DTE intraday job list (user 2026-11-16): every (ROOT, DATE) where ROOT has an expiration ON DATE in the EOD data
(market_full), DATE in [--start, --end], with NEXT = the root's first expiration after DATE that day.
Writes ROOT<TAB>DATE<TAB>NEXT (ISO) for intraday_pull.py --jobs-file.
    cd research && python3 scripts/options/build_0dte_jobs.py --out data/options/intraday_0dte/jobs.tsv
"""
import argparse, glob, os
import duckdb

ROOTS = 'SPY,QQQ,SPXW,IWM,GLD,SLV,TLT,TSLA,NVDA,AAPL,AMZN,META,MSFT'
ap = argparse.ArgumentParser()
ap.add_argument('--roots', default=ROOTS)
ap.add_argument('--start', default='2023-11-13'); ap.add_argument('--end', default='2026-11-13')
ap.add_argument('--eod', default='data/options/market_full')
ap.add_argument('--out', required=True)
a = ap.parse_args()
con = duckdb.connect(); con.sql("set memory_limit='4GB'")
rows = []
for r in a.roots.split(','):
    f = [x for x in glob.glob(f'{a.eod}/{r}__*.parquet') if x.split('__')[1][:4] >= a.start[:4]]
    q = con.sql(f"""with x as (select distinct cast(date as date) d, cast(expiration as date) e from read_parquet({f})
                    where cast(date as date) between '{a.start}' and '{a.end}')
                    select d, min(case when e > d then e end) nxt from x group by d
                    having bool_or(e = d) order by d""").fetchall()
    rows += [(r, str(d), str(n)) for d, n in q if n is not None]
    print(f'{r:5s} {len(q):4d} 0DTE days', flush=True)
os.makedirs(os.path.dirname(a.out) or '.', exist_ok=True)
with open(a.out, 'w') as fo:
    for r, d, n in rows: fo.write(f'{r}\t{d}\t{n}\n')
print(f'{len(rows):,} jobs -> {a.out}')
