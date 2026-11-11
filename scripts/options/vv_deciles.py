#!/usr/bin/env python3
"""Decile breakdown of the three VV features (iv30_rv30, ts_slope_0_45, avg_vol30) on data/options/vv/*.parquet.

Samples (one trade per name-day, entry at the close; exclusions: split in hold, not in universe, gap_days_30 > 5,
missing feature; calendars also need the back leg's exit quote):
  EARN     the VV play: entry the trading day before the reaction (tdays_to_react = 1), front = the first expiry
           holding the event. Straddle + calendar on that front.
  NON30    no event in the hold (and the name HAS an EDGAR source): the straddle nearest 30 DTE; the calendar whose
           front is nearest 30 DTE (back ~60).
  NON7     same, front nearest 7 DTE (the earnings play's shape without the event).
P&L in bp of spot (per share / S x 1e4). mid = mid in & out; nat = sell bid / buy ask; expiry legs settle at intrinsic.
Deciles pooled across names and dates. ⚠ NON samples enter every day: overlapping holds -> naive t-stats overstate.

    cd research && python3 scripts/options/vv_deciles.py
"""
import duckdb, sys
D = 'data/options/vv'
con = duckdb.connect()
con.execute(f"""
create view F as select * from '{D}/features.parquet'
  where in_universe and coalesce(gap_days_30, 99) <= 5 and iv30_rv30 is not null and ts_slope_0_45 is not null and avg_vol30 is not null;
create view S as select s.*, f.iv30_rv30, f.ts_slope_0_45, f.avg_vol30, f.tdays_to_react, f.has_earn_src
  from '{D}/straddles.parquet' s join F f using (root, date) where not s.split_in_hold;
create view C as select c.*, f.iv30_rv30, f.ts_slope_0_45, f.avg_vol30, f.tdays_to_react, f.has_earn_src
  from '{D}/calendars.parquet' c join F f using (root, date) where not c.split_in_hold and c.exit_quote_ok;
-- EARN: tdays_to_react = 1 and the first expiry holding the event
create view S_EARN as select * from S where tdays_to_react = 1 and earn_in_hold >= 1
  qualify row_number() over (partition by root, date order by dte) = 1;
create view C_EARN as select * from C where tdays_to_react = 1 and earn_in_hold >= 1
  qualify row_number() over (partition by root, date order by dte) = 1;
create view S_NON30 as select * from S where has_earn_src and earn_in_hold = 0
  qualify row_number() over (partition by root, date order by abs(dte - 30), dte) = 1;
create view C_NON30 as select * from C where has_earn_src and earn_in_hold = 0 and earn_in_back_only = 0
  qualify row_number() over (partition by root, date order by abs(dte - 30), dte) = 1;
create view S_NON7 as select * from S where has_earn_src and earn_in_hold = 0
  qualify row_number() over (partition by root, date order by abs(dte - 7), dte) = 1;
create view C_NON7 as select * from C where has_earn_src and earn_in_hold = 0 and earn_in_back_only = 0
  qualify row_number() over (partition by root, date order by abs(dte - 7), dte) = 1;
""")
pd = __import__('pandas'); pd.set_option('display.width', 250); pd.set_option('display.max_columns', 30)

def summary(samp):
    print(f"\n=== {samp}: totals ===")
    print(con.sql(f"""
      select 'straddle' kind, count(*) n, count(distinct root) n_names, round(avg(dte),1) dte,
        round(avg(pnl_mid/S)*1e4,1) mid_bp, round(avg(pnl_nat/S)*1e4,1) nat_bp, round(median(pnl_mid/S)*1e4,1) med_mid_bp,
        round(avg((pnl_mid>0)::int)*100,1) win_mid, round(avg(pnl_mid/S)/stddev(pnl_mid/S)*sqrt(count(*)),1) t_mid
      from S_{samp}
      union all
      select 'cal_both', count(*), count(distinct root), round(avg(dte),1),
        round(avg(pnl_mid_s/S)*1e4,1), round(avg(pnl_nat_s/S)*1e4,1), round(median(pnl_mid_s/S)*1e4,1),
        round(avg((pnl_mid_s>0)::int)*100,1), round(avg(pnl_mid_s/S)/stddev(pnl_mid_s/S)*sqrt(count(*)),1)
      from C_{samp}
      union all
      select 'cal_call', count(*), count(distinct root), round(avg(dte),1),
        round(avg(pnl_mid_c/S)*1e4,1), round(avg(pnl_nat_c/S)*1e4,1), round(median(pnl_mid_c/S)*1e4,1),
        round(avg((pnl_mid_c>0)::int)*100,1), round(avg(pnl_mid_c/S)/stddev(pnl_mid_c/S)*sqrt(count(*)),1)
      from C_{samp}""").df().to_string(index=False))

def deciles(samp, feat):
    q = f"""
    with s as (select {feat} x, pnl_mid/S*1e4 m, pnl_nat/S*1e4 nn, pnl_mid/st_mid r from S_{samp}),
         c as (select {feat} x, pnl_mid_s/S*1e4 m, pnl_nat_s/S*1e4 nn from C_{samp}),
         e as (select quantile_cont(x, [0.1,0.2,0.3,0.4,0.5,0.6,0.7,0.8,0.9]) q from s),
         sd as (select (select count(*) from unnest(q) t(v) where x > v) + 1 d, * from s, e),
         cd as (select (select count(*) from unnest(q) t(v) where x > v) + 1 d, * from c, e)
    select sd.d decile, min(x) lo, max(x) hi, count(*) n,
      round(avg(m),1) st_mid, round(avg(nn),1) st_nat, round(avg((m>0)::int)*100,0) st_win, round(median(r)*100,0) st_med_ret,
      any_value(cm) cal_mid, any_value(cn) cal_nat, any_value(cw) cal_win, any_value(cc) cal_n
    from sd left join (select d, round(avg(m),1) cm, round(avg(nn),1) cn, round(avg((m>0)::int)*100,0) cw, count(*) cc
                       from cd group by d) z on z.d = sd.d
    group by sd.d order by sd.d"""
    df = con.sql(q).df()
    fmt = {'iv30_rv30': '{:.2f}', 'ts_slope_0_45': '{:.4f}', 'avg_vol30': '{:.2e}'}[feat]
    df['lo'] = df.lo.map(fmt.format); df['hi'] = df.hi.map(fmt.format)
    print(f"\n--- {samp} by {feat} (edges from the straddle sample; bp of spot) ---")
    print(df.to_string(index=False))

for samp in sys.argv[1:] or ['EARN', 'NON30', 'NON7']:
    summary(samp)
    for f in ['iv30_rv30', 'ts_slope_0_45', 'avg_vol30']:
        deciles(samp, f)
