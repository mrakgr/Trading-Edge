"""LowFader REBUILD step 11 (2026-09-05): the EWMA efficiency twins eff_ewma_10m / eff_ewma_20m (SpikeFader's EwmaEffMa,
half-lives 20 / 40 slot returns; no warmth cliff). Ladders on the 40-100bp base, inside eff_10m<-0.7, and the
substitution test vs the window eff gates. mc=0, PF / PF22, tkd counts. Corpus: data/lowfader_wl_ewma (trip-tkd rerun).
Run: python3 -u scripts/analysis/lowfader_rebuild11.py data/lowfader_wl_ewma 0.010 > data/lowfader_rebuild_11.log
"""
import sys, numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 340)
DIRP = sys.argv[1] if len(sys.argv) > 1 else "data/lowfader_wl_ewma"; VMAX = float(sys.argv[2]) if len(sys.argv) > 2 else 0.010
df = duckdb.query(f"""SELECT symbol, trade_date, signal_sec, ret_exit AS ret, eff_20m, eff_10m, eff_ewma_20m, eff_ewma_10m, eff_ewma_5m
  FROM read_parquet('{DIRP}/*.parquet') WHERE volat_20m > 0.0039 AND volat_20m <= {VMAX}""").df()
df['year']=pd.to_datetime(df['trade_date']).dt.year; YEARS=sorted(df.year.unique()); df['tkd']=df.symbol+'|'+df.trade_date.astype(str)
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def st(g):
    s=g['ret']; s2=g.loc[g.year>=2022,'ret']
    return dict(n=len(s), tkd=g.tkd.nunique(), PF=round(pf(s),3), PF22=round(pf(s2),3) if len(s2) else None, n22=len(s2), win=round((s>0).mean()*100,1), win22=round((s2>0).mean()*100,1) if len(s2) else None, avg=round(s.mean()*100,2), worst=round(s.min()*100,1), tail=round((s<-.2).mean()*100,2))
def yc(g): return {str(y): round(pf(g[g.year==y]['ret']),2) for y in YEARS}
def bands(d, col, edges, name):
    d=d.copy(); d['b']=pd.cut(d[col],edges)
    rows=([dict(band='NaN', **st(d[d[col].isna()]), **yc(d[d[col].isna()]))] if d[col].isna().any() else [])+[dict(band=str(k), **st(g), **yc(g)) for k,g in d.groupby('b',observed=True)]
    print(f'\n--- {name}: {col} ---'); print(pd.DataFrame(rows).to_string(index=False))
print(f"40-{VMAX*1e4:.0f}bp base: {len(df):,} trips {df.tkd.nunique():,} tkd  PF {pf(df.ret):.3f} PF22 {pf(df[df.year>=2022].ret):.3f}")
print('NaN: ' + '  '.join(f"{c} {df[c].isna().mean()*100:.1f}%" for c in ['eff_10m','eff_20m','eff_ewma_10m','eff_ewma_20m','eff_ewma_5m']))
print('quantiles:'); print(df[['eff_10m','eff_ewma_5m','eff_ewma_10m','eff_20m','eff_ewma_20m']].quantile([.05,.25,.5,.75,.95]).round(3).to_string())
print('rho matrix:'); print(df[['eff_10m','eff_ewma_5m','eff_ewma_10m','eff_20m','eff_ewma_20m']].corr().round(3).to_string())
e=[-1.01,-0.9,-0.8,-0.7,-0.6,-0.5,-0.4,-0.3,-0.15,0,0.15,0.3,1.01]
print('\n########## A. ladders on the base ##########')
bands(df,'eff_ewma_5m',e,'base'); bands(df,'eff_ewma_10m',e,'base'); bands(df,'eff_ewma_20m',e,'base')
print('\n########## B. FLOOR sweeps on the base: eff_ewma_Xm < t ##########')
for col in ['eff_ewma_5m','eff_ewma_10m','eff_ewma_20m']:
    rows=[dict(thr='none', **st(df), **yc(df))]
    for t in [-0.3,-0.4,-0.5,-0.6,-0.7,-0.8,-0.9]:
        m=(df[col]<t).fillna(False); rows.append(dict(thr=f'{col}<{t:g}', **st(df[m]), **yc(df[m])))
    print(f'\n--- {col} ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\n########## C. SUBSTITUTION: window gates vs EWMA gates vs both ##########')
w10=(df.eff_10m<-0.7).fillna(False).values; w20=((df.eff_20m<-0.3)|df.eff_20m.isna()).values
rows=[]
for lbl,m in [('window: eff_10m<-0.7 ∧ (eff_20m<-0.3|NaN)',w10&w20),('window: eff_10m<-0.7',w10)]+\
             [(f'ewma_10m<{t:g}',(df.eff_ewma_10m<t).fillna(False).values) for t in [-0.5,-0.6,-0.7]]+\
             [(f'ewma_20m<{t:g}',(df.eff_ewma_20m<t).fillna(False).values) for t in [-0.3,-0.4,-0.5]]+\
             [(f'ewma_5m<{t:g}',(df.eff_ewma_5m<t).fillna(False).values) for t in [-0.6,-0.7,-0.8]]+\
             [(f'window pair ∧ ewma_5m<{t:g}',w10&w20&(df.eff_ewma_5m<t).fillna(False).values) for t in [-0.6,-0.7,-0.8]]+\
             [(f'ewma_10m<{a:g} ∧ ewma_20m<{b:g}',((df.eff_ewma_10m<a)&(df.eff_ewma_20m<b)).fillna(False).values) for a,b in [(-0.6,-0.3),(-0.7,-0.3),(-0.7,-0.4)]]+\
             [(f'window pair ∧ ewma_10m<{t:g}',w10&w20&(df.eff_ewma_10m<t).fillna(False).values) for t in [-0.5,-0.6,-0.7]]+\
             [('window pair ∧ ewma_10m>=-0.6 (what it would cut)',w10&w20&(df.eff_ewma_10m>=-0.6).fillna(False).values)]:
    rows.append(dict(spec=lbl, **st(df[m]), **yc(df[m])))
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## D. eff_ewma_10m ladder INSIDE the window pair ##########')
bands(df[w10&w20],'eff_ewma_5m',e,'window pair'); bands(df[w10&w20],'eff_ewma_10m',e,'window pair'); bands(df[w10&w20],'eff_ewma_20m',e,'window pair')
print('\nDONE')
