"""LowFader REBUILD step 13 (2026-09-05): the LEG COUNTERS. lows_since_first_low_{120,180,300,600} (new lows since the leg's
first low, per window), the session-scale lows/bars/secs_since_first_low, lows_since_uptick, and the rate lows/bars.
On the 40-100bp base and INSIDE eff_ewma_10m < -0.7. mc=0, PF / PF22, tkd. Corpus data/lowfader_wl_ewma.
Run: python3 -u scripts/analysis/lowfader_rebuild13.py data/lowfader_wl_ewma 0.010 > data/lowfader_rebuild_13.log
"""
import sys, numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 340)
DIRP = sys.argv[1] if len(sys.argv) > 1 else "data/lowfader_wl_ewma"; VMAX = float(sys.argv[2]) if len(sys.argv) > 2 else 0.010
C='lows_since_first_low_120, lows_since_first_low_180, lows_since_first_low_300, lows_since_first_low_600, bars_since_first_low_600, lows_since_first_low, bars_since_first_low, secs_since_first_low, lows_since_uptick, eff_ewma_10m'
df = duckdb.query(f"""SELECT symbol, trade_date, ret_exit AS ret, {C} FROM read_parquet('{DIRP}/*.parquet') WHERE volat_20m > 0.0039 AND volat_20m <= {VMAX}""").df()
df['year']=pd.to_datetime(df['trade_date']).dt.year; YEARS=sorted(df.year.unique()); df['tkd']=df.symbol+'|'+df.trade_date.astype(str)
df['rate_600']=df.lows_since_first_low_600/df.bars_since_first_low_600.replace(0,np.nan)
df['rate_sess']=df.lows_since_first_low/df.bars_since_first_low.replace(0,np.nan)
df['mins_since_first_low']=df.secs_since_first_low/60
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def st(g):
    s=g['ret']; s2=g.loc[g.year>=2022,'ret']
    return dict(n=len(s), tkd=g.tkd.nunique(), PF=round(pf(s),3), PF22=round(pf(s2),3) if len(s2) else None, n22=len(s2), win22=round((s2>0).mean()*100,1) if len(s2) else None, avg=round(s.mean()*100,2), worst=round(s.min()*100,1), tail=round((s<-.2).mean()*100,2))
def yc(g): return {str(y): round(pf(g[g.year==y]['ret']),2) for y in YEARS}
def bands(d, col, edges, name):
    d=d.copy(); d['b']=pd.cut(d[col],edges)
    rows=([dict(band='NaN', **st(d[d[col].isna()]), **yc(d[d[col].isna()]))] if d[col].isna().any() else [])+[dict(band=str(k), **st(g), **yc(g)) for k,g in d.groupby('b',observed=True)]
    print(f'\n--- {name}: {col} ---'); print(pd.DataFrame(rows).to_string(index=False))
print('quantiles:'); print(df[['lows_since_first_low_120','lows_since_first_low_300','lows_since_first_low_600','lows_since_first_low','bars_since_first_low','mins_since_first_low','lows_since_uptick','rate_600','rate_sess']].quantile([.05,.25,.5,.75,.95]).round(3).to_string())
ci=[-1,0,1,2,3,5,8,12,18,26,40,60,100,200,1e6]
for name,d in [('base',df),('eff_ewma_10m<-0.7',df[df.eff_ewma_10m<-0.7])]:
    print(f"\n===== {name}: n={len(d):,} tkd={d.tkd.nunique():,} PF {pf(d.ret):.3f} PF22 {pf(d[d.year>=2022].ret):.3f} =====")
    for c in ['lows_since_first_low_120','lows_since_first_low_300','lows_since_first_low_600','lows_since_first_low']: bands(d,c,ci,name)
    bands(d,'lows_since_uptick',[-1,0,1,2,3,5,8,12,20,1e6],name)
    bands(d,'mins_since_first_low',[-1,0,1,2,5,10,20,30,60,120,240,1e6],name)
    bands(d,'rate_600',[-0.01,0.02,0.05,0.1,0.15,0.2,0.3,0.5,1.01],name)
    bands(d,'rate_sess',[-0.01,0.01,0.02,0.05,0.1,0.15,0.2,0.3,0.5,1.01],name)
print('\nDONE')
