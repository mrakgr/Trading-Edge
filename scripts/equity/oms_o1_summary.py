"""§O1 — the fill A/B tables from the OMS ledgers. usage: python oms_o1_summary.py LABEL=outdir [...]"""
import sys, duckdb
con = duckdb.connect()
runs = [a.split("=", 1) for a in sys.argv[1:]]
def q(sql): return con.sql(sql).fetchall()
print("| run | days | equity end | net $ | trades | fill rate (entries placed → filled) | PF $ | PF ret×units | Σ ret×units (%) | avg / median bp | worst trade % | worst day $ | peak units p50 / max | rejects cap / busy / sub$ / cutoff | cross-from / cuts / flattened / stale / re-entries | INV |")
print("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
for label, d in runs:
    dly = q(f"""select count(*), max(equity_close), sum(realized), min(realized), quantile_cont(peak_units,0.5), max(peak_units),
                sum(n_gross_cuts), sum(n_flattened), sum(n_stale), sum(n_reentries), sum(invariant_violations) from read_parquet('{d}/daily.parquet')""")[0]
    pos = q(f"""select count(*), sum(case when pnl>0 then pnl end)/nullif(-sum(case when pnl<0 then pnl end),0), avg(ret)*1e4,
                count(*) filter (where exit_reason='cross_from'),
                sum(case when ret>0 then ret*units end)/nullif(-sum(case when ret<0 then ret*units end),0), sum(ret*units)*100,
                quantile_cont(ret,0.5)*1e4, min(ret)*100 from read_parquet('{d}/positions.parquet')""")[0]
    sig = q(f"""select count(*) filter (where outcome in ('entry_placed','reentry_placed')),
                count(*) filter (where reason='cap'), count(*) filter (where reason in ('ticker_busy','already_open')),
                count(*) filter (where reason='sub_dollar'), count(*) filter (where reason='after_cutoff') from read_parquet('{d}/signals.parquet') where msg <> 'close'""")[0]
    fills = q(f"select count(*) from read_parquet('{d}/orders.parquet') where kind='entry' and event='fill'")[0][0]
    print(f"| {label} | {dly[0]} | {dly[1]:,.0f} | {dly[2]:+,.0f} | {pos[0]:,} | {sig[0]:,} → {fills:,} ({100*fills/max(1,sig[0]):.0f}%) | {pos[1]:.2f} | {pos[4]:.2f} | {pos[5]:+,.0f} | {pos[2]:+.0f} / {pos[6]:+.0f} | {pos[7]:+.1f} | {dly[3]:+,.0f} | {dly[4]:.1f} / {dly[5]:.1f} | {sig[1]:,} / {sig[2]:,} / {sig[3]:,} / {sig[4]:,} | {pos[3]:,} / {dly[6]:,} / {dly[7]:,} / {dly[8]:,} / {dly[9]:,} | {dly[10]} |")
print()
print("| run | system | trades | net $ | PF | avg bp | entries crossed | exits crossed | units mean | hold min |")
print("|---|---|---|---|---|---|---|---|---|---|")
for label, d in runs:
    for r in q(f"""select system, count(*), sum(pnl), sum(case when pnl>0 then pnl end)/nullif(-sum(case when pnl<0 then pnl end),0), avg(ret)*1e4,
                   100*avg(entry_crossed::int), 100*avg(exit_crossed::int), avg(units), avg(exit_sec-entry_sec)/60.0 from read_parquet('{d}/positions.parquet') group by 1 order by 1"""):
        print(f"| {label} | {r[0]} | {r[1]:,} | {r[2]:+,.0f} | {r[3]:.2f} | {r[4]:+.0f} | {r[5]:.0f}% | {r[6]:.0f}% | {r[7]:.2f} | {r[8]:.0f} |")
print()
print("| run | month | trades | net $ | PF | worst day $ |")
print("|---|---|---|---|---|---|")
for label, d in runs:
    for r in q(f"""with p as (select substr(trade_date,1,7) m, count(*) n, sum(pnl) net, sum(case when pnl>0 then pnl end)/nullif(-sum(case when pnl<0 then pnl end),0) pf
                              from read_parquet('{d}/positions.parquet') group by 1),
                        dd as (select substr(trade_date,1,7) m, min(realized) worst from read_parquet('{d}/daily.parquet') group by 1)
                   select p.m, p.n, p.net, p.pf, dd.worst from p join dd using (m) order by 1"""):
        print(f"| {label} | {r[0]} | {r[1]:,} | {r[2]:+,.0f} | {r[3]:.2f} | {r[4]:+,.0f} |")
