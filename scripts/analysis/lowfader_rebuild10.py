"""LowFader REBUILD step 10 (2026-09-05): the eff_9ema (trend-signed) twins, on the base and INSIDE the working spec
SPEC = volat_20m in (40,100]bp ∧ eff_10m < -0.7 ∧ (eff_20m < -0.3 OR NaN). mc=0, PF / PF22, tkd counts.
Run: python3 -u scripts/analysis/lowfader_rebuild10.py data/lowfader_wl_base 0.010 > data/lowfader_rebuild_10.log
"""
import sys, numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 340)
DIRP = sys.argv[1] if len(sys.argv) > 1 else "data/lowfader_wl_base"; VMAX = float(sys.argv[2]) if len(sys.argv) > 2 else 0.010
df = duckdb.query(f"""SELECT symbol, trade_date, signal_sec, ret_exit AS ret, eff_20m, eff_10m, eff_9ema_20m, eff_9ema_10m
  FROM read_parquet('{DIRP}/*.parquet') WHERE volat_20m > 0.0039 AND volat_20m <= {VMAX}""").df()
df['year']=pd.to_datetime(df['trade_date']).dt.year; YEARS=sorted(df.year.unique()); df['tkd']=df.symbol+'|'+df.trade_date.astype(str)
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def st(g):
    s=g['ret']; s2=g.loc[g.year>=2022,'ret']
    return dict(n=len(s), tkd=g.tkd.nunique(), PF=round(pf(s),3), PF22=round(pf(s2),3) if len(s2) else None, n22=len(s2), win=round((s>0).mean()*100,1), win22=round((s2>0).mean()*100,1) if len(s2) else None, avg=round(s.mean()*100,2), worst=round(s.min()*100,1), tail=round((s<-.2).mean()*100,2))
def yc(g): return {str(y): round(pf(g[g.year==y]['ret']),2) for y in YEARS}
spec=((df.eff_10m<-0.7)&((df.eff_20m<-0.3)|df.eff_20m.isna())).fillna(False).values
def bands(d, col, edges, name):
    d=d.copy(); d['b']=pd.cut(d[col],edges)
    rows=([dict(band='NaN', **st(d[d[col].isna()]), **yc(d[d[col].isna()]))] if d[col].isna().any() else [])+[dict(band=str(k), **st(g), **yc(g)) for k,g in d.groupby('b',observed=True)]
    print(f'\n--- {name}: {col} ---'); print(pd.DataFrame(rows).to_string(index=False))
e=[-1.01,-0.5,-0.3,-0.15,0,0.15,0.3,0.45,0.6,0.75,0.9,1.01]
print('quantiles on the SPEC book:'); print(df[spec][['eff_9ema_10m','eff_9ema_20m']].quantile([.05,.25,.5,.75,.95]).round(3).to_string())
print('rho on SPEC: eff_10m~eff_9ema_10m %.3f  eff_20m~eff_9ema_20m %.3f' % (df[spec][['eff_10m','eff_9ema_10m']].corr().iloc[0,1], df[spec][['eff_20m','eff_9ema_20m']].corr().iloc[0,1]))
for name,d in [('40-100bp base',df),('SPEC (eff_10m<-0.7 ∧ (eff_20m<-0.3|NaN))',df[spec])]:
    print(f'\n===== {name}: n={len(d):,} tkd={d.tkd.nunique():,} PF {pf(d.ret):.3f} PF22 {pf(d[d.year>=2022].ret):.3f} =====')
    bands(d,'eff_9ema_10m',e,name); bands(d,'eff_9ema_20m',e,name)
print('\n########## FLOOR sweep on the SPEC: eff_9ema_10m >= t (NaN fails) and the OR-NaN form for the 20m ##########')
d=df[spec]; rows=[dict(thr='none', **st(d), **yc(d))]
for t in [-0.3,-0.1,0,0.15,0.3,0.45,0.6]:
    m=(d.eff_9ema_10m>=t).fillna(False); rows.append(dict(thr=f'9ema_10m>={t:g}', **st(d[m]), **yc(d[m])))
for t in [-0.1,0,0.15,0.3,0.45]:
    m=((d.eff_9ema_20m>=t)|d.eff_9ema_20m.isna()); rows.append(dict(thr=f'9ema_20m>={t:g}|NaN', **st(d[m]), **yc(d[m])))
print(pd.DataFrame(rows).to_string(index=False))
print('\nDONE')
