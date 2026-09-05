"""LowFader REBUILD step 2 (2026-09-05): FlushFader's speed pair inside the 40-60bp volat band.
speed_1m = signal_vwap/vwap_60_prev - 1 ; d1m = signal_vwap/hi_60 - 1 (FlushFader SPEC: both < -2%).
Views: mc=0 first, mc=1 control; year columns = PF-1.
Run: python3 -u scripts/analysis/lowfader_rebuild2.py data/lowfader_wl_base > data/lowfader_rebuild_2.log
"""
import sys, numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 300)
DIRP = sys.argv[1] if len(sys.argv) > 1 else 'data/lowfader_wl_base'
df = duckdb.query(f"""SELECT symbol, trade_date, signal_sec, exit_sec, ret_exit AS ret, volat_20m,
  signal_vwap/NULLIF(vwap_60_prev,0)-1 AS speed_1m, signal_vwap/NULLIF(hi_60,0)-1 AS d1m
  FROM read_parquet('{DIRP}/*.parquet') WHERE volat_20m > 0.0039 AND volat_20m <= 0.006""").df()
df['year']=pd.to_datetime(df['trade_date']).dt.year; YEARS=sorted(df.year.unique())
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def st(s): return dict(n=len(s), PF1=round(pf(s)-1,3), net=round(s.sum()*100), win=round((s>0).mean()*100,1), avg=round(s.mean()*100,2), worst=round(s.min()*100,1), p5=round(s.quantile(.05)*100,1), tail=round((s<-.2).mean()*100,2))
def yc(g): return {str(y): round(pf(g[g.year==y]['ret'])-1,2) for y in YEARS}
def mc1(d):
    d=d.sort_values(['symbol','trade_date','signal_sec']); ss=d['signal_sec'].to_numpy(); es=d['exit_sec'].to_numpy()
    keys=(d['symbol'].astype(str)+'|'+d['trade_date'].astype(str)).to_numpy(); keep=np.zeros(len(d),dtype=bool); busy=-1.0; prev=None
    for i in range(len(d)):
        if keys[i]!=prev: busy=-1.0; prev=keys[i]
        if ss[i]>=busy: keep[i]=True; busy=es[i]
    return d[keep]
print(f"40-60bp band: {len(df):,} trips  {df.groupby(['symbol','trade_date']).ngroups:,} tkd")
print('NaN speed_1m %.2f%%  d1m %.2f%%' % (df.speed_1m.isna().mean()*100, df.d1m.isna().mean()*100))
print('quantiles (%):'); print((df[['speed_1m','d1m']].quantile([.01,.05,.1,.25,.5,.75,.9])*100).round(2).to_string())
edges=[-1,-0.10,-0.06,-0.04,-0.03,-0.02,-0.015,-0.01,-0.005,0,1]
b1=mc1(df)
for col in ['speed_1m','d1m']:
    for name,d in [('mc=0',df),('mc=1 control',b1)]:
        d=d.copy(); d['b']=pd.cut(d[col],edges)
        print(f'\n--- {name}: {col} bands (40-60bp) ---'); print(pd.DataFrame([dict(band=str(k), **st(g['ret']), **yc(g)) for k,g in d.groupby('b',observed=True)]).to_string(index=False))
print('\n########## THE PAIR at a common threshold: speed_1m < t AND d1m < t ##########')
for name,d in [('mc=0',df),('mc=1 control',b1)]:
    rows=[dict(thr='none', **st(d['ret']), **yc(d))]
    for t in [-0.005,-0.01,-0.015,-0.02,-0.03,-0.04,-0.06]:
        m=(d.speed_1m<t)&(d.d1m<t); rows.append(dict(thr=f'<{t*100:g}%', **st(d[m]['ret']), **yc(d[m])))
    print(f'\n--- {name} ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\n########## mc=0 2-D: speed_1m rows x d1m cols (PF-1 / n) ##########')
e2=[-1,-0.04,-0.02,-0.01,0,1]; d=df.copy(); d['s']=pd.cut(d.speed_1m,e2); d['d']=pd.cut(d.d1m,e2)
print(d.groupby(['s','d'],observed=True)['ret'].apply(lambda s: f"{pf(s)-1:.2f}/{len(s)}").unstack().to_string())
print('\nDONE')
