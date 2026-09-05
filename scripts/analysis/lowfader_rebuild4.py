"""LowFader REBUILD step 4 (2026-09-05): the volume features inside the 40-60bp volat band.
vol_vs_high = vol_60 / vol_60_prior_max (LowFlyer's volume-confirm; >= 0.90 in production)
rr = vol_60 / (vol_0945_tape/15)  (SpikeFader/MaxFader's brv15_tape: 1m volume vs the avg 1m volume of the first 15 min)
Run: python3 -u scripts/analysis/lowfader_rebuild4.py data/lowfader_wl_base > data/lowfader_rebuild_4.log
"""
import sys, numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 300)
DIRP = sys.argv[1] if len(sys.argv) > 1 else 'data/lowfader_wl_base'
df = duckdb.query(f"""SELECT symbol, trade_date, signal_sec, exit_sec, ret_exit AS ret,
  vol_60/NULLIF(vol_60_prior_max,0) AS vvh, vol_60/NULLIF(vol_0945_tape/15.0,0) AS rr
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
b1=mc1(df)
print(f"40-60bp band: {len(df):,} trips"); print('NaN vvh %.2f%%  rr %.2f%%' % (df.vvh.isna().mean()*100, df.rr.isna().mean()*100))
print('quantiles:'); print(df[['vvh','rr']].quantile([.05,.1,.25,.5,.75,.9,.95,.99]).round(3).to_string())
print('rho(vvh, rr) = %.3f   rho(log vvh, log rr) = %.3f' % (df[['vvh','rr']].corr().iloc[0,1], np.log(df[['vvh','rr']].clip(lower=1e-6)).corr().iloc[0,1]))
def bands(col, edges):
    for name,d in [('mc=0',df),('mc=1 control',b1)]:
        d=d.copy(); d['b']=pd.cut(d[col],edges)
        rows=[dict(band='NaN', **st(d[d[col].isna()]['ret']), **yc(d[d[col].isna()]))]+[dict(band=str(k), **st(g['ret']), **yc(g)) for k,g in d.groupby('b',observed=True)]
        print(f'\n--- {name}: {col} bands (40-60bp) ---'); print(pd.DataFrame(rows).to_string(index=False))
def floors(col, ts):
    for name,d in [('mc=0',df),('mc=1 control',b1)]:
        rows=[dict(thr='none', **st(d['ret']), **yc(d))]
        for t in ts:
            m=d[col]>=t; rows.append(dict(thr=f'>={t:g} ({m.mean()*100:.0f}%)', **st(d[m]['ret']), **yc(d[m])))
        print(f'\n--- {name}: FLOOR {col} >= t ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\n########## A. vol_vs_high = vol_60 / vol_60_prior_max ##########')
bands('vvh',[0,0.1,0.2,0.3,0.5,0.7,0.9,1.0,1.5,2,3,5,100])
floors('vvh',[0.3,0.5,0.7,0.9,1.0,1.5,2,3])
print('\n########## B. rr = vol_60 / (vol_0945_tape/15) ##########')
bands('rr',[0,0.25,0.5,1,2,4,8,12,20,40,1e6])
floors('rr',[0.5,1,2,4,8,12,20])
print('\n########## C. mc=0 2-D: vvh rows x rr cols (PF-1 / n) ##########')
d=df.copy(); d['v']=pd.cut(d.vvh,[0,0.3,0.9,1.5,3,100]); d['r']=pd.cut(d.rr,[0,1,4,12,40,1e6])
print(d.groupby(['v','r'],observed=True)['ret'].apply(lambda s: f"{pf(s)-1:.2f}/{len(s)}").unstack().to_string())
print('\n--- modern era (2022+) ---'); m=d[d.year>=2022]
print(m.groupby(['v','r'],observed=True)['ret'].apply(lambda s: f"{pf(s)-1:.2f}/{len(s)}").unstack().to_string())
print('\nDONE')
