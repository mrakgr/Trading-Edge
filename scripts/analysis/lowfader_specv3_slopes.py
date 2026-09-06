"""LowFader SPEC v3 (+l120>=30): PRICE SLOPES / rates of change as a which-bar gate to replace the ordinal (2026-09-05 late night, user).
ols_slope_N = OLS slope of ln(vwap) per BAR over the last N present bars (x6e5 -> bp/min); ols_r_N its correlation; since_high/since_flow
anchored versions; z_since_high; chg_since_{last_uptick,run_pre_low,run_first_low,run_first_dn}. mc=1 (MOC | 10m), mc=5 MOC, 2022+.
Run: python3 -u scripts/analysis/lowfader_specv3_slopes.py > data/lowfader_specv3_slopes.log
"""
import numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 340)
G=['data/lowfader_alltape_h1/*.parquet','data/lowfader_alltape_h1c/*.parquet','data/lowfader_alltape_h1b/*.parquet','data/lowfader_alltape_h2/*.parquet','data/lowfader_alltape_h2e/*.parquet','data/lowfader_alltape_h2b/*.parquet','data/lowfader_alltape_h2c/*.parquet','data/lowfader_alltape_h2d/*.parquet']
CUTW=' AND '.join(f"NOT (t.filename LIKE '%{k}%' AND CAST(t.trade_date AS DATE) >= DATE '{v}')" for k,v in {'_h1/':'2022-01-19','_h2/':'2024-12-06','_h2b/':'2025-09-23'}.items())
S=['ols_slope_60','ols_slope_120','ols_slope_180','ols_slope_300','ols_slope_600','ols_slope_1200','ols_slope_since_high','ols_slope_since_flow','ols_slope_hi_flow']
R=['ols_r_60','ols_r_120','ols_r_300','ols_r_600','ols_r_1200','ols_r_since_high','z_since_high','z_since_flow','chg_since_last_uptick','chg_since_run_pre_low','chg_since_run_first_low','chg_since_run_first_dn','bars_since_high','secs_since_first_low']
c=duckdb.connect('data/trading.db', read_only=True); c.execute("SET memory_limit='8GB'")
df=c.execute(f"""SELECT t.symbol, t.trade_date, t.signal_sec, t.entry_sec, t.exit_sec, t.entry_px, t.ret_exit AS ret, t.aux_hi_600_px, t.aux_hi_600_sec, {', '.join('t.'+x for x in S+R)}
  FROM read_parquet({G}, filename=true) t JOIN mr_candidate_1s_v2 m ON m.ticker=t.symbol AND m.date=CAST(t.trade_date AS DATE) AND m.barnum >= 22
  WHERE {CUTW} AND t.volat_20m > 0.005 AND t.volat_20m <= 0.010 AND t.eff_ewma_10m < -0.7 AND t.lows_since_first_low_600 >= 40 AND t.lows_since_first_low_120 >= 30
    AND t.lows_since_first_low_600 >= 0.15*t.bars_since_first_low_600 AND t.vol_60 >= 2.0*t.vol_0945_tape/15.0 AND t.entry_px/NULLIF(t.close_m1+coalesce(t.div_m1,0),0)-1 <= -0.04
    AND t.gap_adj_60 <= 30 AND t.dollar_vol_60 >= 1e6""").df()
df['year']=pd.to_datetime(df.trade_date).dt.year; YEARS=sorted(df.year.unique()); df['tkd']=df.symbol+'|'+df.trade_date.astype(str)
df=df.sort_values(['tkd','signal_sec']).reset_index(drop=True); df['ord']=df.groupby('tkd').cumcount()+1
f=df.aux_hi_600_px.notna(); df['ret10']=np.where(f, df.aux_hi_600_px/df.entry_px-1, df.ret); df['x10']=np.where(f, df.aux_hi_600_sec, df.exit_sec)
for s in S: df[s+'_bpm']=df[s]*6e5   # log-price per bar -> bp per minute
df['mins_since_first_low']=df.secs_since_first_low/60
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def mck(d, x, k):
    ss=d['signal_sec'].to_numpy(); es=d[x].to_numpy(); keys=d['tkd'].to_numpy(); keep=np.zeros(len(d),bool); open_ex=[]; prev=None
    for i in range(len(d)):
        if keys[i]!=prev: open_ex=[]; prev=keys[i]
        open_ex=[e for e in open_ex if e>ss[i]]
        if len(open_ex)<k: keep[i]=True; open_ex.append(es[i])
    return d[keep]
def row(lbl, m):
    d=df[m]; r=dict(gate=lbl, mc0_n=len(d), tkd22=d[d.year>=2022].tkd.nunique(), med_ord=d.ord.median(), pct_ord1=round((d.ord==1).mean()*100,1))
    for ex,cc,x in [('MOC','ret','exit_sec'),('10m','ret10','x10')]:
        b=mck(d,x,1); b2=b[b.year>=2022]
        r.update({f'mc1_{ex}_tr_yr':round(len(b)/6.63,1), f'mc1_{ex}_PF1':round(pf(b2[cc])-1,2) if len(b2) else None, f'mc1_{ex}_net':round(b2[cc].sum()*100), f'mc1_{ex}_worst':round(b[cc].min()*100,1) if len(b) else None})
    b=mck(d,'exit_sec',5); b2=b[b.year>=2022]; r.update(dict(mc5_tr_yr=round(len(b)/6.63,1), mc5_PF1=round(pf(b2.ret)-1,2) if len(b2) else None, mc5_net=round(b2.ret.sum()*100), mc5_worst=round(b.ret.min()*100,1) if len(b) else None))
    return r
print(f"SPEC v3 + l120>=30: {len(df):,} trips, {df.tkd.nunique()} tkd, {df[df.year>=2022].tkd.nunique()} tkd22")
print('reference:'); print(pd.DataFrame([row('all (1st bar at mc=1)',np.ones(len(df),bool)), row('ordinal>=4',(df.ord>=4).values)]).to_string(index=False))
print('\nslope quantiles (bp/min):'); print(df[[s+'_bpm' for s in S]].quantile([.05,.25,.5,.75,.95]).round(1).to_string())
print('rho with ordinal:', {s: round(df[[s+'_bpm','ord']].corr().iloc[0,1],3) for s in S})
print('\n########## A. slope LADDERS (bp/min; NEGATIVE = falling): mc=1 MOC PF-1 | net ; mc=5 PF-1 | net ; median ordinal ##########')
for s in S:
    col=s+'_bpm'; d=df.copy(); q=d[col].quantile([.1,.25,.5,.75,.9]).tolist(); edges=sorted(set([-1e9]+[round(x,1) for x in q]+[1e9]))
    d['b']=pd.cut(d[col],edges); rows=[]
    for k,g in d.groupby('b',observed=True):
        if len(g)<20: continue
        b1=mck(g,'exit_sec',1); b12=b1[b1.year>=2022]; b5=mck(g,'exit_sec',5); b52=b5[b5.year>=2022]
        rows.append(dict(band=str(k), n=len(g), med_ord=g.ord.median(), mc1_PF1=round(pf(b12.ret)-1,2) if len(b12) else None, mc1_net=round(b12.ret.sum()*100), mc5_PF1=round(pf(b52.ret)-1,2) if len(b52) else None, mc5_net=round(b52.ret.sum()*100), mc5_worst=round(b5.ret.min()*100,1) if len(b5) else None))
    print(f'\n--- {col} (quantile bands) ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\n########## B. FLOOR/CEILING sweeps as which-bar gates ##########')
rows=[]
for s in ['ols_slope_60','ols_slope_120','ols_slope_300','ols_slope_600','ols_slope_1200','ols_slope_since_high']:
    col=s+'_bpm'; q=df[col].quantile([.25,.5,.75]).tolist()
    for lbl,m in [(f'{s} <= p25 ({q[0]:.0f})',(df[col]<=q[0]).fillna(False).values),(f'{s} <= p50 ({q[1]:.0f})',(df[col]<=q[1]).fillna(False).values),(f'{s} >= p50 ({q[1]:.0f})',(df[col]>=q[1]).fillna(False).values),(f'{s} >= p75 ({q[2]:.0f})',(df[col]>=q[2]).fillna(False).values)]:
        rows.append(row(lbl,m))
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## C. the rate-of-change / anchor columns (quantile bands, mc=1 MOC | mc=5) ##########')
for col in ['chg_since_run_first_low','chg_since_run_pre_low','chg_since_last_uptick','chg_since_run_first_dn','z_since_high','ols_r_1200','ols_r_300','bars_since_high','mins_since_first_low']:
    d=df.copy(); q=d[col].quantile([.1,.25,.5,.75,.9]).tolist(); edges=sorted(set([-1e9]+q+[1e9]))
    if len(edges)<3: continue
    d['b']=pd.cut(d[col],edges); rows=[]
    for k,g in d.groupby('b',observed=True):
        if len(g)<20: continue
        b1=mck(g,'exit_sec',1); b12=b1[b1.year>=2022]; b5=mck(g,'exit_sec',5); b52=b5[b5.year>=2022]
        rows.append(dict(band=str(k), n=len(g), med_ord=g.ord.median(), mc1_PF1=round(pf(b12.ret)-1,2) if len(b12) else None, mc1_net=round(b12.ret.sum()*100), mc5_PF1=round(pf(b52.ret)-1,2) if len(b52) else None, mc5_net=round(b52.ret.sum()*100), mc5_worst=round(b5.ret.min()*100,1) if len(b5) else None))
    print(f'\n--- {col} ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\nDONE')
