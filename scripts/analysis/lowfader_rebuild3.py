"""LowFader REBUILD step 3 (2026-09-05): chg_20m = signal_vwap/vwap_1200 - 1 inside the 40-60bp volat band.
Run: python3 -u scripts/analysis/lowfader_rebuild3.py data/lowfader_wl_base > data/lowfader_rebuild_3.log
"""
import sys, numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 300)
DIRP = sys.argv[1] if len(sys.argv) > 1 else 'data/lowfader_wl_base'
df = duckdb.query(f"""SELECT symbol, trade_date, signal_sec, exit_sec, ret_exit AS ret, volat_20m,
  signal_vwap/NULLIF(vwap_60_prev,0)-1 AS speed_1m, signal_vwap/NULLIF(vwap_1200,0)-1 AS chg_20m
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
print(f"40-60bp band: {len(df):,} trips"); print('NaN chg_20m %.2f%%' % (df.chg_20m.isna().mean()*100))
print('quantiles (%):'); print((df['chg_20m'].quantile([.01,.05,.1,.25,.5,.75,.9,.95])*100).round(2).to_string())
print('rho(chg_20m, speed_1m) = %.3f' % df[['chg_20m','speed_1m']].corr().iloc[0,1])
edges=[-1,-0.15,-0.10,-0.07,-0.05,-0.04,-0.03,-0.02,-0.015,-0.01,-0.005,0,1]
b1=mc1(df)
for name,d in [('mc=0',df),('mc=1 control',b1)]:
    d=d.copy(); d['b']=pd.cut(d['chg_20m'],edges)
    print(f'\n--- {name}: chg_20m bands (40-60bp) ---'); print(pd.DataFrame([dict(band=str(k), **st(g['ret']), **yc(g)) for k,g in d.groupby('b',observed=True)]).to_string(index=False))
print('\n########## FLOOR sweep: chg_20m <= t ##########')
for name,d in [('mc=0',df),('mc=1 control',b1)]:
    rows=[dict(thr='none', **st(d['ret']), **yc(d))]
    for t in [-0.01,-0.02,-0.03,-0.04,-0.05,-0.07,-0.10]:
        m=d.chg_20m<=t; rows.append(dict(thr=f'<={t*100:g}%', **st(d[m]['ret']), **yc(d[m])))
    print(f'\n--- {name} ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\n########## mc=0 2-D: chg_20m rows x speed_1m cols (PF-1 / n) ##########')
d=df.copy(); d['c']=pd.cut(d.chg_20m,[-1,-0.07,-0.04,-0.02,-0.01,0,1]); d['s']=pd.cut(d.speed_1m,[-1,-0.04,-0.02,-0.01,0,1])
print(d.groupby(['c','s'],observed=True)['ret'].apply(lambda s: f"{pf(s)-1:.2f}/{len(s)}").unstack().to_string())
print('\n########## mc=0 2-D: chg_20m rows x YEAR cols, modern era only (2022+), PF-1 / n ##########')
m=d[d.year>=2022]; print(m.groupby(['c','year'],observed=True)['ret'].apply(lambda s: f"{pf(s)-1:.2f}/{len(s)}").unstack().to_string())
print('\nDONE')
